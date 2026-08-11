using Cordia.Application.Handlers.Notifications;
using Cordia.Application.Handlers.Notifications.Reconcile;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Unit tests for <see cref="ReconcileNotificationCommandHandler"/>
/// (spec 10 §12.8 / §13.6 capability ladder, T4).
///
/// All deps are mocked; the IUnitOfWork mock invokes the delegate so the handler body runs.
/// </summary>
[Trait("Category", "Unit")]
public class ReconcileNotificationTests
{
    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    private readonly Mock<INotificationRepository> _repo = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<INotificationTemplateResolver> _templateResolver = new();
    private readonly Mock<INotificationRecipientResolver> _recipientResolver = new();

    // T5a: retry scheduler deps (no-op mocks for tests that don't exercise the transient path)
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IRetryPolicy> _retryPolicy = new();
    private readonly Mock<INotificationRepository> _retryRepo = new();
    private readonly Mock<IAuditEventRepository> _retryAuditRepo = new();

    private const string TenantId = "tenant-reconcile-test";
    private const string NotificationId = "notif-reconcile-1";
    private const string ProviderKey = "pkey-reconcile-1";
    private const string ProviderMsgId = "pmsg_reconcile_abc";

    public ReconcileNotificationTests()
    {
        // Make the UoW mock invoke the delegate so the handler body actually runs.
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task> work, CancellationToken ct) => work(ct));

        _auditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _retryAuditRepo.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default template resolver returns a stub (F10: reconcile resend uses non-empty body).
        _templateResolver.Setup(r => r.ResolveAsync(
                It.IsAny<NotificationEvent>(), It.IsAny<NotificationChannel>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationTemplate("v1", "stub-body"));

        // Default: the operation's revision carries a guest, so a resend has somewhere to go.
        // Without a Destination the handler settles InvalidRecipient instead of calling the
        // adapter — deliberately, so a resend can never claim a delivery it did not attempt.
        _recipientResolver.Setup(r => r.ResolveAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedRecipient(
                "recip_abc123", NotificationChannel.Sms, "en", Destination: "+61400000000"));

        // Default: policy returns a next time so transient calls don't crash (used only in
        // TransientOnResend test; other tests never hit this path).
        _retryPolicy.Setup(p => p.NextEligibleAt(
                It.IsAny<StepRetryBudget>(),
                It.IsAny<int>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>()))
            .Returns(DateTimeOffset.UtcNow.AddSeconds(10));
        _retryRepo.Setup(r => r.MarkTransientRetryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<NotificationState>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _jobs.Setup(j => j.ExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _jobs.Setup(j => j.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private NotificationRetryScheduler BuildRetryScheduler() =>
        new(_jobs.Object, _retryPolicy.Object, _retryRepo.Object, _retryAuditRepo.Object,
            NullLogger<NotificationRetryScheduler>.Instance);

    private ReconcileNotificationCommandHandler BuildHandler(
        params INotificationProviderAdapter[] adapters)
        => new(
            _recipientResolver.Object,
            _repo.Object,
            adapters,
            _auditRepo.Object,
            _uow.Object,
            BuildRetryScheduler(),
            _templateResolver.Object,
            NullLogger<ReconcileNotificationCommandHandler>.Instance);

    private static Notification MakeSendingNotification(
        string providerIdempotencyKey = ProviderKey,
        long version = 1)
        => new()
        {
            TenantId = TenantId,
            NotificationId = NotificationId,
            OperationId = "op-test",
            State = NotificationState.Sending,
            Channel = NotificationChannel.Sms,
            Priority = MessagePriority.High,
            ProviderIdempotencyKey = providerIdempotencyKey,
            RecipientRef = "recip-test",
            TemplateVersion = "v1",
            MaterialRevision = 1,
            Version = version,
        };

    private static Mock<INotificationProviderAdapter> MakeAdapter(
        NotificationChannel channel,
        bool supportsLookup,
        bool supportsSendIdempotency)
    {
        var mock = new Mock<INotificationProviderAdapter>();
        mock.Setup(a => a.Channel).Returns(channel);
        mock.Setup(a => a.SupportsLookup).Returns(supportsLookup);
        mock.Setup(a => a.SupportsSendIdempotency).Returns(supportsSendIdempotency);
        return mock;
    }

    // ── 1. Lookup branch (FM#4): SupportsLookup=true, LookupByKeyAsync→Accepted ────────────────

    [Fact]
    public async Task LookupBranch_FoundAccepted_ReturnsAcceptedViaLookup_NoResend_AuditsLookup()
    {
        var n = MakeSendingNotification();
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 1L,
            NotificationState.Sending, ProviderMsgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: true, supportsSendIdempotency: true);
        adapter.Setup(a => a.LookupByKeyAsync(ProviderKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, ProviderMsgId, null));

        var cmd = new ReconcileNotificationCommand(TenantId, NotificationId);
        var result = await BuildHandler(adapter.Object).Handle(cmd, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.AcceptedViaLookup, result);

        // No resend — SendAsync must NOT be called.
        adapter.Verify(a => a.SendAsync(
            It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // MarkAcceptedAsync called with the lookup's provider_message_id.
        _repo.Verify(r => r.MarkAcceptedAsync(TenantId, NotificationId, 1L,
            NotificationState.Sending, ProviderMsgId, It.IsAny<CancellationToken>()), Times.Once);

        // Audit notification.reconciled_lookup (not duplicate_risk).
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.reconciled_lookup"),
            It.IsAny<CancellationToken>()), Times.Once);
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.duplicate_risk"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 2. Send-idempotency branch: SupportsLookup=false, SupportsSendIdempotency=true, SendAsync→Accepted ──

    [Fact]
    public async Task SendIdempotencyBranch_SendAccepted_ReturnsAcceptedViaResend_NoDupRiskAudit()
    {
        var n = MakeSendingNotification();
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 1L,
            NotificationState.Sending, ProviderMsgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: false, supportsSendIdempotency: true);
        adapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, ProviderMsgId, null));

        var cmd = new ReconcileNotificationCommand(TenantId, NotificationId);
        var result = await BuildHandler(adapter.Object).Handle(cmd, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.AcceptedViaResend, result);

        // LookupByKeyAsync must NOT be called (SupportsLookup=false).
        adapter.Verify(a => a.LookupByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // No duplicate-risk audit (send-idempotency makes it safe).
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.duplicate_risk"),
            It.IsAny<CancellationToken>()), Times.Never);

        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.reconciled_resend"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── 3. Neither-capability branch (FM#5): both flags false, SendAsync→Accepted ───────────────

    [Fact]
    public async Task NeitherCapabilityBranch_SendAccepted_ReturnsAcceptedViaResendDuplicateRisk_AuditsDupRisk()
    {
        var n = MakeSendingNotification();
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 1L,
            NotificationState.Sending, ProviderMsgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: false, supportsSendIdempotency: false);
        adapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, ProviderMsgId, null));

        var cmd = new ReconcileNotificationCommand(TenantId, NotificationId);
        var result = await BuildHandler(adapter.Object).Handle(cmd, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.AcceptedViaResendDuplicateRisk, result);

        // notification.duplicate_risk audit was emitted (FM#5).
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.duplicate_risk"),
            It.IsAny<CancellationToken>()), Times.Once);

        // No claim of exactly-once — only the dup_risk audit.
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.reconciled_lookup"),
            It.IsAny<CancellationToken>()), Times.Never);
        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.reconciled_resend"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 4. Lookup supported but not-found: SupportsLookup=true, LookupByKeyAsync→null ───────────

    [Fact]
    public async Task LookupSupportedButNotFound_FallsThrough_ResendAccepted_ReturnsAcceptedViaResend()
    {
        var n = MakeSendingNotification();
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 1L,
            NotificationState.Sending, ProviderMsgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: true, supportsSendIdempotency: true);
        // Lookup returns null (not found on provider side).
        adapter.Setup(a => a.LookupByKeyAsync(ProviderKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((NotificationSendResult?)null);
        // Falls through to resend.
        adapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, ProviderMsgId, null));

        var cmd = new ReconcileNotificationCommand(TenantId, NotificationId);
        var result = await BuildHandler(adapter.Object).Handle(cmd, CancellationToken.None);

        // Lookup+send-idempotency: resend is the first real send, no dup risk.
        Assert.Equal(ReconcileOutcome.AcceptedViaResend, result);

        // Both lookup and send were called.
        adapter.Verify(a => a.LookupByKeyAsync(ProviderKey, It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Once);

        _auditRepo.Verify(a => a.AddAsync(
            It.Is<AuditEvent>(e => e.EventType == "notification.duplicate_risk"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 5. Row not Sending → NotSending, no adapter calls ─────────────────────────────────────

    [Fact]
    public async Task RowNotSending_ReturnsNotSending_NoAdapterCalls()
    {
        var n = new Notification
        {
            TenantId = TenantId,
            NotificationId = NotificationId,
            OperationId = "op-test",
            State = NotificationState.Accepted,
            Channel = NotificationChannel.Sms,
            Version = 2,
        };
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: true, supportsSendIdempotency: true);

        var cmd = new ReconcileNotificationCommand(TenantId, NotificationId);
        var result = await BuildHandler(adapter.Object).Handle(cmd, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.NotSending, result);

        adapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        adapter.Verify(a => a.LookupByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 6. Transient failure on resend → T5a schedules retry; no booking write (Inv-8) ──────────

    [Fact]
    public async Task TransientOnResend_ReturnsFailed_SchedulesRetry_NoBookingWrite()
    {
        var n = MakeSendingNotification();
        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: false, supportsSendIdempotency: true);
        adapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.FailedTransient, null, "timeout"));

        var cmd = new ReconcileNotificationCommand(TenantId, NotificationId);
        var result = await BuildHandler(adapter.Object).Handle(cmd, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result);

        // T5a: retryScheduler.MarkTransientRetryAsync is called (not direct MarkFailedAsync FailedTransient)
        _retryRepo.Verify(r => r.MarkTransientRetryAsync(
            TenantId, NotificationId, 1L, NotificationState.Sending,
            It.IsAny<DateTimeOffset>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Direct MarkFailedAsync(FailedTransient) must NOT be called
        _repo.Verify(r => r.MarkFailedAsync(TenantId, NotificationId, 1L,
            NotificationState.Sending, NotificationState.FailedTransient,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Inv-8: handler never writes to booking_operations.
    }

    // ── 7. Waitlist recovery: OperationId is NULL by design (spec 10) ───────────────────────────

    /// <summary>
    /// A waitlist offer/expiry row stores <c>OperationId = null</c> — spec 10 ratifies it as "null
    /// for non-operation messages", because the <c>op_wl_*</c> operation does not exist until the
    /// offer is accepted and a non-null value would violate the composite FK.
    ///
    /// <para>Reconcile used to read that null as "no recipient" and settle
    /// <c>InvalidRecipient</c> — which is TERMINAL. A guest the provider had actually reached was
    /// permanently recorded unreachable and the offer was never re-attempted. The enrollment id on
    /// <c>correlation_id</c> is the durable link back to them.</para>
    /// </summary>
    [Fact]
    public async Task WaitlistRow_NullOperation_ResolvesDestinationFromEnrollmentCorrelation()
    {
        const string enrollmentId = "wle_abc123";

        var n = MakeSendingNotification();
        n.OperationId = null;
        n.CorrelationId = enrollmentId;
        n.EventType = NotificationEvent.WaitlistOffer;

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkAcceptedAsync(TenantId, NotificationId, 1L,
                NotificationState.Sending, ProviderMsgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _recipientResolver.Setup(r => r.ResolveByEnrollmentAsync(
                TenantId, enrollmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedRecipient(
                "recip_enrollment", NotificationChannel.Sms, "en", Destination: "+61400111222"));

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: false, supportsSendIdempotency: true);
        adapter.Setup(a => a.SendAsync(ProviderKey, It.IsAny<RenderedMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationSendResult(NotificationSendOutcome.Accepted, ProviderMsgId, null));

        var result = await BuildHandler(adapter.Object)
            .Handle(new ReconcileNotificationCommand(TenantId, NotificationId), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.AcceptedViaResend, result);

        // The guest is actually reached — not settled as unreachable.
        adapter.Verify(a => a.SendAsync(ProviderKey, It.Is<RenderedMessage>(
            m => m.Destination == "+61400111222"), It.IsAny<CancellationToken>()), Times.Once);

        _repo.Verify(r => r.MarkFailedAsync(TenantId, NotificationId, It.IsAny<long>(),
            It.IsAny<NotificationState>(), NotificationState.InvalidRecipient,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Inv-9: the row's persisted ref keys notification_id and must survive verbatim — the
        // enrollment-derived ref the resolver returns is NOT written back over it.
        adapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.Is<RenderedMessage>(
            m => m.MinimizedRecipient == "recip-test"), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Fails closed: a row with no operation AND no correlation still has nowhere to deliver, so
    /// the no-destination guard must behave exactly as it did before the fallback existed.
    /// </summary>
    [Fact]
    public async Task WaitlistRow_NullOperation_NoCorrelation_StillSettlesInvalidRecipient()
    {
        var n = MakeSendingNotification();
        n.OperationId = null;
        n.CorrelationId = null;
        n.EventType = NotificationEvent.WaitlistOffer;

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkFailedAsync(TenantId, NotificationId, 1L,
                NotificationState.Sending, NotificationState.InvalidRecipient,
                "provider_invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: false, supportsSendIdempotency: true);

        var result = await BuildHandler(adapter.Object)
            .Handle(new ReconcileNotificationCommand(TenantId, NotificationId), CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result);

        // Inv-16: never claim a delivery that was not attempted.
        adapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.MarkFailedAsync(TenantId, NotificationId, 1L,
            NotificationState.Sending, NotificationState.InvalidRecipient,
            "provider_invalid", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The enrollment fallback is gated on the waitlist event types, not merely on a null
    /// operation. `correlation_id` is a generic column, so a future non-waitlist event that leaves
    /// `OperationId` null — or any producer that stores a trace id there — must NOT be able to
    /// resolve a destination from an unrelated enrollment and message a stranger.
    /// </summary>
    [Fact]
    public async Task NullOperation_OnANonWaitlistEvent_NeverConsultsTheEnrollmentFallback()
    {
        var n = MakeSendingNotification();
        n.OperationId = null;
        // A real enrollment id sitting in correlation_id, but the wrong event type for it to mean
        // that — precisely the confusion the gate exists to refuse.
        n.CorrelationId = "wle_someone_else";
        n.EventType = NotificationEvent.BookingConfirmed;

        _repo.Setup(r => r.GetAsync(TenantId, NotificationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(n);
        _repo.Setup(r => r.MarkFailedAsync(TenantId, NotificationId, 1L,
                NotificationState.Sending, NotificationState.InvalidRecipient,
                "provider_invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _recipientResolver.Setup(r => r.ResolveByEnrollmentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedRecipient(
                "recip_stranger", NotificationChannel.Sms, "en", Destination: "+61400999999"));

        var adapter = MakeAdapter(NotificationChannel.Sms, supportsLookup: false, supportsSendIdempotency: true);

        await BuildHandler(adapter.Object)
            .Handle(new ReconcileNotificationCommand(TenantId, NotificationId), CancellationToken.None);

        // The fallback was never even asked, and nothing was sent to the stranger.
        _recipientResolver.Verify(r => r.ResolveByEnrollmentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        adapter.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<RenderedMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
