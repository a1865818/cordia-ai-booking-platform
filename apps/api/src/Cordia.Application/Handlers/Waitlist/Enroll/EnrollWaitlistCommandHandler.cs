using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Exceptions;
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
    IWaitlistOfferRepository offerRepo,
    IBookingOperationRepository opRepo,
    IAuditEventRepository auditRepo,
    IIdempotencyRepository idempotency,
    ICanonicalRequestHasher hasher,
    IUnitOfWork uow)
    : IRequestHandler<EnrollWaitlistCommand, string>
{
    /// <summary>
    /// An explicit, caller-supplied key gets the acceptance path's window (§13.3) — it must
    /// outlive every replay path that could reach it.
    /// </summary>
    private static readonly TimeSpan ExplicitKeyWindow = TimeSpan.FromDays(365);

    /// <summary>
    /// An AUTO-DERIVED key gets a short one, and that difference matters.
    ///
    /// The derived key is a hash of the enrolment's own fields, so it collides with any future
    /// enrolment carrying the same details. Over a year that is not duplicate protection, it is
    /// a trap: a guest who enrols, cancels, and rings back next week with identical details is
    /// not retrying anything. The window only needs to cover an operator retrying after an
    /// ambiguous failure — minutes, not months.
    ///
    /// <para>The window alone was never enough, and the gap it left is now closed separately:
    /// a remove-then-re-add takes SECONDS, so it lands well inside any sane window. The replay
    /// path below therefore checks that the recorded enrolment is still live before answering
    /// with it (Inv-16). The window bounds what counts as a retry; the liveness check is what
    /// stops a cancelled enrolment being reported as a live one.</para>
    /// </summary>
    private static readonly TimeSpan DerivedKeyWindow = TimeSpan.FromHours(1);

    public async Task<string> Handle(EnrollWaitlistCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var enrollmentId = $"wle_{Guid.NewGuid():N}";

        // Duplicate protection (docs §12 gap 6).
        //
        // Enrolment had NONE: an ambiguous network failure followed by an operator retry created
        // a second `wle_*` row, and the guest sat on the waitlist twice — two offers, two holds
        // against the same capacity, and no error anywhere to notice it by.
        //
        // This reuses `idempotency_records` rather than inventing a mechanism: the table is
        // already `unique(tenant_id, idempotency_key)` with an ON CONFLICT DO NOTHING creator
        // election, which is exactly the primitive needed. No new DDL, and the same-key /
        // different-payload conflict (Inv-3) comes along with it via the request hash.
        //
        // When the caller supplies no key, one is derived from the enrolment's own material
        // fields — so a blind retry of an IDENTICAL enrolment still deduplicates, which is the
        // case that actually bites. A deliberate second enrolment differs in some field (or
        // carries its own key) and is correctly allowed through.
        // EVERY material field, not a subset — this is the Inv-3 binding, and a field left out of
        // it is a field a caller can silently change on a reused key.
        //
        // It used to hash six of the twelve. `notification_channel` and `offer_lifetime_seconds`
        // are the ones that bit: both are real enrolment parameters (the first decides where the
        // offer is delivered, the second when it expires), both are editable in the console's own
        // drawer, and neither was hashed. So "the response was lost, correct the channel, press
        // retry" matched the stored hash, was answered as a replay, and returned 201 for the
        // ORIGINAL enrolment — the guest kept the old channel and nothing anywhere said so.
        // `auto_book` mattered more quietly: a retry that withdrew a consent grant kept it.
        //
        // Widening cannot cost the derived-key path anything, which is why there was never a
        // reason to narrow it: a blind retry re-sends byte-identical fields, so it still
        // deduplicates. It only stops a DIFFERENT request being mistaken for that retry.
        //
        // Deposit/auto-book are hashed by their JSON so a change inside either is a change to the
        // request, matching how the acceptance path hashes its whole canonical payload.
        var requestHash = hasher.Hash(JsonSerializer.SerializeToNode(new
        {
            customer_ref = command.CustomerRef,
            service_id = command.ServiceId,
            location_id = command.LocationId,
            party_size = command.PartySize,
            acceptable_from = command.AcceptableFrom,
            acceptable_to = command.AcceptableTo,
            notification_channel = command.NotificationChannel,
            offer_lifetime_seconds = command.OfferLifetimeSeconds,
            deposit_terms = command.Deposit?.ToJson(),
            auto_book = command.AutoBook?.ToJson(),
            identity_ref = command.IdentityRef,
            verification_level = command.VerificationLevel,
        }));
        var derived = string.IsNullOrWhiteSpace(command.IdempotencyKey);
        var idempotencyKey = derived
            ? $"wle_auto_{requestHash.Value}"
            : command.IdempotencyKey!;
        var window = derived ? DerivedKeyWindow : ExplicitKeyWindow;

        var existing = await idempotency.GetByKeyAsync(command.TenantId, idempotencyKey, ct);

        // A derived key past its window is a DIFFERENT intent that happens to look the same, not
        // a retry of this one, so the enrolment must go through.
        //
        // Nulling `existing` is NOT enough to make that happen, and this is easy to miss: the
        // expired record still occupies `unique(tenant_id, idempotency_key)`, so the creator
        // election below would lose on conflict and the lost-election path would hand back the
        // OLD enrolment id — precisely the outcome the window exists to prevent. The record only
        // disappears when the retention sweep next runs, which is a background job and cannot be
        // a correctness dependency of the next request. So an expired derived key is treated as a
        // spent fence and voided, exactly like a cancelled one.
        string? spentKeyOperationId = null;
        if (existing is not null && derived && existing.CreatedAt < now - DerivedKeyWindow)
        {
            spentKeyOperationId = existing.OperationId;
            existing = null;
        }

        if (existing is not null
            && existing.RequestHash != $"{requestHash.Version}:{requestHash.Value}")
        {
            throw new IdempotencyConflictException(
                idempotencyKey,
                existing.OperationId,
                $"Idempotency key '{idempotencyKey}' is already bound to a different enrolment.");
        }

        // Same key, same hash — a replay. It is answerable with the recorded enrolment ONLY while
        // that enrolment still exists.
        //
        // The derived key is what makes the bad case reachable: it is a hash of the guest's own
        // details, so enrol → remove → re-enrol with the same details inside the window collides
        // with the very enrolment it just reversed. Returning that id would report "added to the
        // waitlist" for a guest who is on no list, with no error anywhere to notice it by — an
        // outcome the platform cannot back (Inv-16). The 1-hour window narrows this; it does not
        // close it, because a remove-then-re-add takes seconds.
        //
        // A missing row is treated the same as a cancelled one: the record would be a fence
        // around nothing, and honouring it returns a 201 for an id that 404s on every read. Both
        // are safe to re-enrol because the hash already matched — an enrolment's hash covers EVERY
        // material field of the enrolment, so a record written by any other feature cannot reach
        // here. Deliberately count-free: it said "six" while the hash covered twelve, and any
        // widening would have re-staled a number.
        if (existing is not null)
        {
            var recorded = await repo.GetAsync(existing.OperationId, ct);

            // Tenant re-check (Inv-13). `GetAsync` takes no tenant id and relies on the ambient
            // global query filter; the cancel handler re-asserts it the same way rather than
            // trusting one layer. A row belonging to another tenant is treated as absent, so it
            // can neither be returned as this tenant's enrolment nor have its fence voided.
            if (recorded is not null && recorded.TenantId != command.TenantId)
                recorded = null;

            if (recorded is not null && recorded.CancelledAt is null)
                return existing.OperationId; // live replay: spec 04's disposition, unchanged

            // A terminal enrollment is not automatically a SPENT one. `CancelledAt` carries two
            // outcomes that could not be more different: the guest withdrew, or the guest was
            // BOOKED — the accept-reconcile path stamps the same column to make a fulfilled
            // enrollee ineligible for a second offer.
            //
            // Treating fulfilment as a spent fence re-elected a fresh `wle_*` row on an identical
            // retry, so a guest who already has a table was put back on the waitlist: eligible for
            // another offer, or under an AutoBookGrant a silent second booking. An explicit key
            // makes that reachable for a year.
            //
            // An accepted offer whose booking actually LANDED is the evidence — the offer alone is
            // not. An offer is claimed Accepted before the booking is confirmed and has no
            // transition out again, so an accept whose booking then failed terminally leaves the
            // enrollee correctly eligible; treating that as fulfilment would answer a later genuine
            // withdraw-and-re-enrol with the CANCELLED id, reporting "added to the waitlist" for a
            // guest on no list — the same Inv-16 outcome this whole block exists to prevent.
            //
            // Fulfilled → answer with the recorded enrollment (the replay disposition) rather than
            // creating a second; anything else voids the fence and re-enrols.
            if (recorded is not null
                && await WasFulfilledAsync(command.TenantId, existing.OperationId, ct))
            {
                return existing.OperationId;
            }

            spentKeyOperationId = existing.OperationId;
        }

        var won = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // Release the spent fence before electing, conditional on it still pointing at the
            // dead enrolment. If a concurrent re-enrolment already re-bound the key, this affects
            // 0 rows and the election below loses — falling through to the lost-election path
            // rather than deleting the winner's fresh record and duplicating the guest.
            if (spentKeyOperationId is not null)
            {
                await idempotency.TryVoidAsync(
                    command.TenantId, idempotencyKey, spentKeyOperationId, ct2);
            }

            won = await idempotency.ElectCreatorAsync(new IdempotencyRecord
            {
                TenantId = command.TenantId,
                IdempotencyKey = idempotencyKey,
                // Enrolment creates a record; the enum has no waitlist member and D1 is frozen.
                OperationType = OperationTypeEnum.Create,
                RequestHash = $"{requestHash.Version}:{requestHash.Value}",
                OperationId = enrollmentId,
                ProcessingStatus = IdempotencyStatus.Completed,
                CreatedAt = now,
                LastSeenAt = now,
                RetentionDeadline = now + window,
            }, ct2);

            // Lost the race: a concurrent identical enrolment won. Write nothing — the winner's
            // row is the enrolment, and creating a second here is the exact duplicate this
            // guards against.
            if (!won)
                return;

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

        if (won)
            return enrollmentId;

        // Lost the election: this call wrote NOTHING, so returning the id it minted would hand
        // the caller a 201 for a row that does not exist and 404s on every subsequent read. The
        // winner's record holds the real id — the acceptance path resolves the same race the
        // same way.
        var winner = await idempotency.GetByKeyAsync(command.TenantId, idempotencyKey, ct);
        return winner?.OperationId
            ?? throw new IdempotencyConflictException(
                idempotencyKey,
                enrollmentId,
                "Lost the enrolment creator election but the winning record could not be read.");
    }

    /// <summary>
    /// Whether a terminal enrollment ended because the enrollee was BOOKED, rather than because
    /// they withdrew. True only when an accepted offer's operation actually reached
    /// <see cref="OperationStatus.Booked"/> — which is the same condition
    /// <c>ReconcileWaitlistAcceptCommandHandler</c> uses before marking the enrollment fulfilled.
    /// </summary>
    private async Task<bool> WasFulfilledAsync(
        string tenantId, string enrollmentId, CancellationToken ct)
    {
        var operationIds = await offerRepo.GetAcceptedOfferOperationIdsAsync(tenantId, enrollmentId, ct);

        foreach (var operationId in operationIds)
        {
            var op = await opRepo.GetAsync(operationId, ct);
            if (op is not null && op.TenantId == tenantId && op.Status == OperationStatus.Booked)
                return true;
        }

        return false;
    }
}
