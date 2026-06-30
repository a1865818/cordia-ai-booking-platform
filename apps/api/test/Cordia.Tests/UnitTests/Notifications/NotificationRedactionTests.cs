using Cordia.Application.Handlers.Notifications;
using Cordia.Application.Handlers.Notifications.SendNotification;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using Cordia.Infrastructure.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.RegularExpressions;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Inv-12 regression guard: proves that rendered message body + audit metadata for
/// <see cref="NotificationEvent.ActionRequired"/> (the deposit/payment path) never
/// contain card data (PAN, CVV/CVC) and that <see cref="RenderedMessage.MinimizedRecipient"/>
/// is the opaque handle, not a raw phone/email (spec 10 §7, §27.6, T6).
/// </summary>
[Trait("Category", "Unit")]
public class NotificationRedactionTests
{
    // Regex: 13-19 consecutive decimal digits that would resemble a PAN.
    private static readonly Regex PanPattern = new(@"\d{13,19}", RegexOptions.Compiled);

    private readonly Mock<INotificationRepository> _repo = new();
    private readonly Mock<INotificationRecipientResolver> _recipientResolver = new();
    private readonly Mock<IConsentGate> _consentGate = new();
    private readonly Mock<INotificationTemplateResolver> _templateResolver = new();
    private readonly Mock<INotificationProviderAdapter> _smsAdapter = new();
    private readonly Mock<IBookingOperationRepository> _opRepo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IRetryPolicy> _retryPolicy = new();
    private readonly Mock<INotificationRepository> _retryRepo = new();
    private readonly Mock<IAuditEventRepository> _retryAuditRepo = new();

    private const string TenantId = "tenant-redaction-test";
    private const string OperationId = "op-redaction-test";
    private const string MessageId = "msg-redaction-test";

    // Opaque minimized recipient handle — must NOT be a raw phone/email.
    private const string OpaqueRecipientRef = "recip_abc123opaque";

    private NotificationRetryScheduler BuildRetryScheduler() =>
        new(_jobs.Object, _retryPolicy.Object, _retryRepo.Object, _retryAuditRepo.Object,
            NullLogger<NotificationRetryScheduler>.Instance);

    private SendNotificationCommandHandler BuildHandler() =>
        new(
            _repo.Object,
            _recipientResolver.Object,
            _consentGate.Object,
            _templateResolver.Object,
            new[] { _smsAdapter.Object },
            _opRepo.Object,
            _auditRepo.Object,
            BuildRetryScheduler(),
            NullLogger<SendNotificationCommandHandler>.Instance,
            new Metrics());

    /// <summary>
    /// Drive SendNotificationCommandHandler for an ActionRequired (deposit) event.
    /// Capture the RenderedMessage passed to the adapter via a Moq callback and assert:
    /// (1) Body contains no 13-19 consecutive digit sequence resembling a PAN.
    /// (2) Body contains no "cvv" or "cvc" substring (case-insensitive).
    /// (3) Body for ActionRequired carries only a link placeholder ({payment_link}), not raw card data.
    /// (4) MinimizedRecipient equals the opaque recip_… handle, not any raw phone/email.
    /// (5) The RecipientRef persisted in the notification entity is the opaque handle.
    /// </summary>
    [Fact]
    public async Task ActionRequired_RenderedMessage_ContainsNoPanOrCvv_RecipientIsOpaqueHandle()
    {
        // ── Arrange ──────────────────────────────────────────────────────────────────────────────

        var op = new BookingOperation
        {
            TenantId = TenantId,
            OperationId = OperationId,
            IntentId = "int-redact-1",
            IntentRevision = 1,
            Status = OperationStatus.Booked,
            Version = 0,
        };

        _opRepo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);

        _recipientResolver
            .Setup(r => r.ResolveAsync(TenantId, OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedRecipient(OpaqueRecipientRef, NotificationChannel.Sms, "en"));

        _consentGate
            .Setup(c => c.CheckAsync(TenantId, OpaqueRecipientRef, NotificationChannel.Sms, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConsentDecision.Allowed);

        // Template resolver returns the safe ActionRequired body (Inv-12: link placeholder only).
        const string expectedBody =
            "Action required: please complete your payment using the secure link provided. Do not share this link. {payment_link}";
        _templateResolver
            .Setup(t => t.ResolveAsync(
                NotificationEvent.ActionRequired,
                NotificationChannel.Sms,
                "en",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationTemplate("action_required.sms.v1", expectedBody));

        _repo.Setup(r => r.TryInsertAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repo.Setup(r => r.TryTransitionStateAsync(
                TenantId, It.IsAny<string>(), 0L,
                NotificationState.Pending, NotificationState.Sending,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _smsAdapter.SetupGet(a => a.Channel).Returns(NotificationChannel.Sms);
        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Capture the RenderedMessage delivered to the adapter.
        RenderedMessage? capturedRendered = null;
        _smsAdapter
            .Setup(a => a.SendAsync(
                It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, RenderedMessage, CancellationToken>((_, rendered, _) =>
                capturedRendered = rendered)
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, "pmsg_redact_01", null));

        _repo.Setup(r => r.MarkAcceptedAsync(
                TenantId, It.IsAny<string>(), 1L, NotificationState.Sending,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var command = new SendNotificationCommand(
            TenantId, OperationId, MessageId,
            NotificationEvent.ActionRequired, MessagePriority.Critical,
            null);

        // ── Act ───────────────────────────────────────────────────────────────────────────────────

        await BuildHandler().Handle(command, CancellationToken.None);

        // ── Assert ────────────────────────────────────────────────────────────────────────────────

        Assert.NotNull(capturedRendered);
        var body = capturedRendered!.Body;
        var minimizedRecipient = capturedRendered.MinimizedRecipient;

        // (1) No PAN-like sequence (13-19 consecutive digits) in the body.
        Assert.False(
            PanPattern.IsMatch(body),
            $"Body must not contain a PAN-like digit sequence but was: '{body}'");

        // (2) No CVV/CVC substring (case-insensitive).
        Assert.DoesNotContain("cvv", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cvc", body, StringComparison.OrdinalIgnoreCase);

        // (3) Body equals the expected safe ActionRequired copy (link placeholder only, no card data).
        Assert.Equal(expectedBody, body);

        // (4) MinimizedRecipient is the opaque handle, not a raw phone/email.
        Assert.Equal(OpaqueRecipientRef, minimizedRecipient);
        // Opaque handle must start with the 'recip_' prefix convention and contain no '@' or raw digits
        // that would indicate a phone number or email address in plaintext.
        Assert.StartsWith("recip_", minimizedRecipient, StringComparison.Ordinal);
        Assert.DoesNotContain("@", minimizedRecipient, StringComparison.Ordinal);

        // (5) RecipientRef persisted in the notification entity is the opaque handle.
        _repo.Verify(r => r.TryInsertAsync(
            It.Is<Notification>(n =>
                n.RecipientRef == OpaqueRecipientRef &&
                n.TenantId == TenantId),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
