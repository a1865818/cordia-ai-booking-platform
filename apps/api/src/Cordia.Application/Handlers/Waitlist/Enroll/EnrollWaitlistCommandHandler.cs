using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Enroll;

/// <summary>
/// Persists a new waitlist enrollment and audits it in one transaction (spec 11 §4.4). Matching is
/// opening-triggered (a freed slot enqueues <c>waitlist.commands.match.v1</c>), so enrollment does
/// not itself enqueue a match — a fresh enrollment simply becomes eligible for the next opening.
/// </summary>
public sealed class EnrollWaitlistCommandHandler(
    IWaitlistEnrollmentRepository repo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<EnrollWaitlistCommand, string>
{
    public async Task<string> Handle(EnrollWaitlistCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var enrollmentId = $"wle_{Guid.NewGuid():N}";

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await repo.AddAsync(new WaitlistEnrollment
            {
                TenantId = command.TenantId,
                EnrollmentId = enrollmentId,
                CustomerRef = command.CustomerRef,
                AcceptableFrom = command.AcceptableFrom,
                AcceptableTo = command.AcceptableTo,
                PartySize = command.PartySize,
                LocationId = command.LocationId,
                ServiceId = command.ServiceId,
                OfferLifetimeSeconds = command.OfferLifetimeSeconds,
                DepositTerms = command.Deposit?.ToJson(),
                NotificationChannel = command.NotificationChannel,
                AutoBook = command.AutoBook?.ToJson(),
                // Divergence #1: capture identity context for the accept→confirmation mint (Inv-1).
                IdentityRef = string.IsNullOrEmpty(command.IdentityRef) ? command.CustomerRef : command.IdentityRef,
                VerificationLevel = string.IsNullOrEmpty(command.VerificationLevel) ? "unverified" : command.VerificationLevel,
                CreatedAt = now,
            }, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "waitlist.enrolled",
                Actor = command.Actor,
                SubjectRef = enrollmentId,
                CorrelationId = enrollmentId,
                OperationId = null,
                Metadata = JsonSerializer.Serialize(new
                {
                    customer_ref = command.CustomerRef,
                    location_id = command.LocationId,
                    service_id = command.ServiceId,
                    party_size = command.PartySize,
                    auto_book = command.AutoBook is not null,
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return enrollmentId;
    }
}
