using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Application.Common;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Preorder;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using MediatR;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;

/// <summary>
/// Orchestrates the durable acceptance transaction (spec 04 §10.3, Inv-4).
///
/// Disposition logic (§5.1, §13.4):
///   1. Compute canonical hash H over the revision canonical payload.
///   2. Confirm that confirmation.intent_payload_hash == H.Value (§8.8, Inv-1); reject 422 otherwise.
///   3. Pre-check the idempotency store:
///      - Existing row, same hash  → replay 202 from snapshot.
///      - Existing row, diff hash  → 409 Conflict (Inv-3).
///      - No row                   → attempt creator election.
///   4. Inside one UoW transaction: INSERT idempotency_records via ON CONFLICT DO NOTHING.
///      - Won election (1 row): write intent/revision/confirmation/operation/outbox/audit; return 202.
///      - Lost election (0 rows): reload winner and apply §5.1 disposition; return 202 or 409.
/// </summary>
public sealed class SubmitConfirmedIntentCommandHandler(
    ICanonicalRequestHasher hasher,
    IUnitOfWork uow,
    IIdempotencyRepository idempotencyRepo,
    IBookingIntentRepository intentRepo,
    IBookingOperationRepository operationRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    Metrics metrics,
    IConversationSessionRepository sessions,
    IVerificationGrantRepository grants,
    IBookingRepository bookingRepo,
    IGrantAuthorizationService grantAuth,
    IOptions<AssertedActorOptions> assertedActor)
    : IRequestHandler<SubmitConfirmedIntentCommand, AcceptanceResult>
{
    // §13.3: retain idempotency records for booking life + dispute/audit window.
    // Concrete value pending tenant/jurisdiction policy; one year is the conservative floor.
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(365);

    public async Task<AcceptanceResult> Handle(
        SubmitConfirmedIntentCommand command,
        CancellationToken ct)
    {
        var request = command.Request;
        var revision = request.Revision;
        var confirmation = request.Confirmation;

        // --- Step 1: Compute canonical hash H over the canonical payload. ---
        // Must use Converter.Settings so union types (DurationMinutes, StartsAt) serialise back to
        // their wire form (number / string / TriState string) rather than the struct layout that
        // default STJ would produce.  This keeps the hash consistent with what the Python agent
        // computed over the original JSON: JsonNode.Parse(rawJson) → CanonicalRequestHasher.Hash.
        var payloadNode = JsonSerializer.SerializeToNode(revision.CanonicalPayload, Cordia.Domain.Generated.Converter.Settings);
        var H = hasher.Hash(payloadNode);

        // --- Step 2: Confirmation hash check (§8.8, Inv-1). ---
        if (confirmation.IntentPayloadHash != H.Value)
            throw new ConfirmationHashMismatchException(
                $"confirmation.intent_payload_hash '{confirmation.IntentPayloadHash}' does not match the computed canonical hash '{H.Value}'.");

        // requestHashStored = version-prefixed (Inv-3 / §5 — preserves canon version without a schema change).
        var requestHashStored = $"{H.Version}:{H.Value}";

        // Map BookingRequest.Operation (OperationTypeEnum from generated namespace) to
        // the Domain.Generated OperationTypeEnum — they are the same type (same assembly);
        // no mapping needed.
        var operationType = revision.CanonicalPayload.Operation;

        // --- Step 2b: authorize a mutation of an existing booking (Inv-11). ---
        // Ahead of the idempotency pre-check on purpose: a replay hands back the winner's
        // operation id, so an unauthorized caller must not reach it either.
        //
        // Returns the grant that authorized this request, when one did. Non-null means the grant
        // was checked against the spec 08 SCOPE — tenant, customer, action, target — which is what
        // makes it safe to derive the recorded assurance level from it below.
        //
        // Scope, deliberately NOT liveness: `RequireGrantScopeAsync` checks neither consumption nor
        // expiry, so this must not be read as "the grant is still live". Liveness is enforced
        // atomically where it belongs — `ConsumeGrantAsync` at commit, which is what a request that
        // goes on to create a NEW operation has to pass, and which denies a spent or expired grant
        // by affecting zero rows. Keeping the pre-check scope-only is what lets an honest retry of
        // an already-accepted request still find its operation instead of a 403.
        var authorizingGrant = await AuthorizeMutationAsync(
            command, operationType, revision.CanonicalPayload.TargetBookingId, confirmation.Identity, ct);

        // --- Step 3: Pre-check the idempotency store (fast path, outside transaction). ---
        var existing = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.IdempotencyKey, ct);
        if (existing != null)
        {
            if (existing.RequestHash == requestHashStored)
                return BuildReplay(existing);

            // Inv-3: key reused with a different payload — alert signal (§25.4).
            metrics.AcceptanceIdempotencyConflictTotal.Add(1);
            return new Conflict(new IdempotencyConflict
            {
                IdempotencyKey = request.IdempotencyKey,
                ExistingOperationId = existing.OperationId,
                Message = "Idempotency key already bound to a different request.",
            });
        }

        // --- Step 4: Build the acceptance snapshot (before the transaction). ---
        var now = DateTimeOffset.UtcNow;

        // R15 — the assurance level is the SERVER's to state, not the client's.
        //
        // `confirmation.verification_level` is a string the caller sends. The validator now
        // rejects an off-vocabulary value, but a well-formed lie ("high_assurance" from a lane
        // that verified nobody) would still be recorded verbatim. When `confirmation.identity`
        // names a real, unexpired grant, the grant is the platform's own record of how well that
        // party was verified — so it wins (Inv-11: authorization assurance is server-verified).
        //
        // Only looked up for a grant-shaped identity: the console sends an operator id, and a
        // query that can never match should not sit on the p95<300ms acceptance path (§10.3).
        var assertedLevel = ConfirmationMappings.MapVerificationLevel(confirmation.VerificationLevel);

        // The level is derived from the grant that ACTUALLY AUTHORIZED this request.
        //
        // The lookup used to consider only a `grant_`-prefixed `confirmation.identity`, so a
        // caller who authorized with `X-Grant-Id` — the normal way an agent-lane mutation is
        // authorized — fell straight through to the level they had ASSERTED in the body. The one
        // request carrying real server-side proof of assurance was the one that ignored it, and
        // `unverified`/`high_assurance` was whatever the client typed (Inv-11).
        //
        // `ResolveGrantId` is the same resolution `AuthorizeMutationAsync` performs, so the level
        // recorded and the grant relied upon can never come from different places. The
        // identity-prefix path is retained as the fast path for callers who put the grant there.
        var effectiveGrantId = ResolveGrantId(command.GrantId, confirmation.Identity);

        // The grant must belong to THIS request's customer before its level is believed.
        //
        // A mutation is already bound: `AuthorizeMutationAsync` checked the grant against the
        // booking's customer, action and target, so a non-null `authorizingGrant` is proof.
        //
        // A create has no authorization gate — nothing existing is being changed — so nothing
        // stopped a caller putting SOMEONE ELSE'S unexpired grant id in `confirmation.identity`
        // and having its (higher) level recorded as though the platform had verified them to it.
        // The canonical payload names the customer the booking is for, so when it does, that is
        // the binding: a grant issued to a different customer says nothing about this one and its
        // level is ignored. When the payload names nobody there is nothing to contradict, and the
        // grant remains the best evidence available — which is the case the derivation was added
        // for (an agent-lane customer who verified and then under-claims in the body must not be
        // recorded as unverified and re-challenged later).
        VerificationLevel? grantedLevel = null;
        if (!string.IsNullOrWhiteSpace(effectiveGrantId))
        {
            grantedLevel = authorizingGrant?.GrantId == effectiveGrantId
                // This grant was scope-checked against the booking's customer, action and target.
                ? await grants.GetAssuranceLevelAsync(command.TenantId, effectiveGrantId, now, ct)
                // Merely named. Bind it to the mutation's customer when there is one (the staff-role
                // path lands here), otherwise to the customer the create's payload names.
                : await GetLevelForNamedGrantAsync(
                    command.TenantId,
                    effectiveGrantId,
                    authorizingGrant?.CustomerRef,
                    revision.CanonicalPayload.Customer,
                    now,
                    ct);
        }

        var verificationLevel = grantedLevel ?? assertedLevel;
        var snapshot = new OperationAcceptedResponse
        {
            OperationId = request.OperationId,
            IntentId = revision.IntentId,
            IntentRevision = revision.Revision,
            Status = "accepted",
            StatusUrl = $"/api/booking-operations/{request.OperationId}",
            AcceptedAt = now,
        };
        var snapshotJson = JsonSerializer.Serialize(snapshot);

        var record = new IdempotencyRecord
        {
            TenantId = command.TenantId,
            IdempotencyKey = request.IdempotencyKey,
            OperationType = operationType,
            RequestHash = requestHashStored,
            OperationId = request.OperationId,
            ProcessingStatus = IdempotencyStatus.Completed,
            ResponseSnapshot = snapshotJson,
            CreatedAt = now,
            LastSeenAt = now,
            RetentionDeadline = now + RetentionWindow,
        };

        // --- Step 5: Creator election + atomic writes inside one UoW transaction. ---
        bool won = await TryElectAndWriteAsync(record, command, revision, confirmation, operationType, request, H.Value, verificationLevel, authorizingGrant, now, ct);

        if (!won)
        {
            // This request lost the concurrent creator race — reload the winner's record.
            // If the winner is null it was deleted by retention in the race window, making
            // the key free again. Retry the whole election up to 2 more times before giving
            // up; a bounded loop guards against spinning while remaining safe.
            const int MaxRetries = 2;
            IdempotencyRecord? winner = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                winner = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.IdempotencyKey, ct);
                if (winner != null)
                    break;

                if (attempt == MaxRetries)
                    throw new InvalidOperationException(
                        $"Creator election lost for key '{request.IdempotencyKey}' but the winner record was not found after {MaxRetries + 1} attempts (retention race).");

                // Key is free again — re-run the election inside a new transaction.
                won = await TryElectAndWriteAsync(record, command, revision, confirmation, operationType, request, H.Value, verificationLevel, authorizingGrant, now, ct);

                if (won)
                    return new Accepted(snapshot);
            }

            if (winner!.RequestHash == requestHashStored)
                return BuildReplay(winner);

            // Inv-3: key reused with a different payload (post-election race) — alert signal (§25.4).
            metrics.AcceptanceIdempotencyConflictTotal.Add(1);
            return new Conflict(new IdempotencyConflict
            {
                IdempotencyKey = request.IdempotencyKey,
                ExistingOperationId = winner.OperationId,
                Message = "Idempotency key already bound to a different request.",
            });
        }

        return new Accepted(snapshot);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Authorizes a mutation of an existing booking submitted through the acceptance front door
    /// (spec 08 §12: "every mutation accept requires a valid <c>verification_grant</c> — or an
    /// authenticated owner role — scoped to (tenant, customer, target booking, action, expiry)";
    /// Inv-11).
    ///
    /// <para><b>Why this door needed it.</b> <c>POST /api/Bookings/{id}/modifications</c> and
    /// <c>.../cancellations</c> both consume a grant, but neither carries the changed data —
    /// <c>SubmitModificationInput</c> is <c>additionalProperties: false</c> with no field for it.
    /// A reschedule therefore has to ride THIS door, which is the only one that transports the new
    /// time. Until the fulfilment engine could actually move a booking that was harmless; now that
    /// it can, an unauthenticated caller who knew a booking id could move someone else's booking.</para>
    ///
    /// <para><b>Two ways in, matching the two lanes.</b> The agent lane holds a platform-issued
    /// grant (<c>verify_identity</c> obtains one before a mutation is submittable) and already
    /// carries its id on <c>confirmation.identity</c>, which is where the assurance-level
    /// derivation above reads it from; it may also present <c>X-Grant-Id</c>. The console acts as
    /// authenticated staff and presents <c>X-Actor</c>/<c>X-Actor-Role</c>. Either satisfies the
    /// ratified rule; neither is optional. A create is untouched — there is no existing booking to
    /// authorize against.</para>
    ///
    /// <para>The identity fallback is not "trusting what the caller said": the id is looked up in
    /// <c>verification_grants</c> and must be unexpired and scoped to this tenant, customer,
    /// booking and action. An unscoped or invented id authorizes nothing.</para>
    ///
    /// <para>Fails closed: an unknown role, a blank actor, an absent grant, a grant scoped to
    /// another booking/action, and an unknown target all deny. Grant denials are audited inside
    /// <see cref="IGrantAuthorizationService"/>; the role and unknown-target branches raise the
    /// same 403 so a caller cannot distinguish "wrong grant" from "no such booking".</para>
    /// </summary>
    private async Task<AuthorizingGrant?> AuthorizeMutationAsync(
        SubmitConfirmedIntentCommand command,
        OperationTypeEnum operationType,
        string? targetBookingId,
        string? confirmationIdentity,
        CancellationToken ct)
    {
        var action = operationType switch
        {
            // Reschedule IS a modify (spec 08 §9.3) — the same action gates both.
            OperationTypeEnum.Modify or OperationTypeEnum.Reschedule => VerificationActionEnum.Modify,
            OperationTypeEnum.Cancel => VerificationActionEnum.Cancel,
            // create / inquire — nothing existing is being changed.
            _ => (VerificationActionEnum?)null,
        };

        if (action is null)
            return null;

        // Staff acting under an asserted role authorize a MODIFY directly. Operator suffices:
        // moving a booking is ordinary front-desk work, not an owner-only commercial decision
        // (GD-1).
        //
        // Deliberately not extended to cancel. Cancel already has a door that transports
        // everything it needs — `POST /api/Bookings/{id}/cancellations` — and that door consumes a
        // guest grant with no role alternative. Accepting a role here would make this the weaker
        // way to perform the identical operation, which is how an authorization requirement gets
        // routed around rather than enforced. A reschedule has no such alternative: the
        // manage-booking door cannot carry a new time, so the role path is the only one it has.
        // The validator requires target_booking_id for exactly these operations, so a blank one
        // here means the request never should have reached the handler — deny rather than
        // silently accept an unauthorizable mutation.
        var booking = string.IsNullOrWhiteSpace(targetBookingId)
            ? null
            : await bookingRepo.GetByIdAsync(command.TenantId, targetBookingId, ct);

        // ...and only where an asserted role is allowed to mean anything.
        //
        // `StaffRole.Satisfies` validates the VOCABULARY, not the caller: `X-Actor-Role` is a
        // string the client chose. With the branch unconditionally live, anyone who knew a booking
        // id could send `X-Actor-Role: operator` and reschedule another customer's booking with no
        // grant — the requirement routed around rather than enforced, on the one door a reschedule
        // must use. Gated exactly like the `X-Tenant-Id` shim it depends on (see
        // <see cref="AssertedActorOptions"/>): on in Development, off elsewhere unless a deployment
        // opts in, at which point a mutation needs a real grant.
        if (action.Value == VerificationActionEnum.Modify
            && assertedActor.Value.AllowAssertedStaffRole
            && !string.IsNullOrWhiteSpace(command.ActorId)
            && StaffRole.Satisfies(command.ActorRole, StaffRole.Operator))
        {
            // The existence check is NOT skipped for the role path. It used to return above this
            // lookup, so a staff-asserted reschedule naming a booking that does not exist — or one
            // belonging to another tenant — was accepted, given an intent revision and an
            // operation, and only came apart later inside ModifyBookingCommandHandler, by which
            // point the caller had been told the mutation was taken. Establishing the target
            // exists is not the grant check; both paths need it.
            if (booking is null)
            {
                await grantAuth.WriteDenyAsync(
                    command.TenantId, command.GrantId, customerRef: string.Empty, action.Value,
                    targetBookingId, targetType: "booking", reason: "unknown_target_booking",
                    actorId: command.ActorId, actorRole: command.ActorRole);
                throw new VerificationRequiredException(
                    command.TenantId, customerRef: string.Empty, action.Value, targetBookingId);
            }

            // The staff-role path authorizes on the ASSERTED ROLE, so there is no grant to spend —
            // but the booking's customer is still the binding for any grant the caller happens to
            // name. Returning null here left `effectiveGrantId` non-empty with nothing to check it
            // against, so a role-asserted caller could put any live grant id in `X-Grant-Id` and
            // have its level recorded: the same hole this change closes for creates, on the path
            // with the weakest authorization of the two.
            return new AuthorizingGrant(
                GrantId: null, booking.CustomerId, action.Value, booking.BookingId);
        }

        if (booking is null || string.IsNullOrWhiteSpace(booking.CustomerId))
        {
            await grantAuth.WriteDenyAsync(
                command.TenantId, command.GrantId, customerRef: string.Empty, action.Value,
                targetBookingId, targetType: "booking",
                reason: booking is null ? "unknown_target_booking" : "booking_has_no_customer",
                actorId: command.ActorId, actorRole: command.ActorRole);
            throw new VerificationRequiredException(
                command.TenantId, customerRef: string.Empty, action.Value, targetBookingId);
        }

        var grantId = ResolveGrantId(command.GrantId, confirmationIdentity);

        // Scope-checked now; SPENT at commit (see TryElectAndWriteAsync). Consuming here would
        // refuse the caller's own idempotent retry, because the first attempt's success is what
        // spent the grant — so the fence has to sit where the new operation is actually created.
        await grantAuth.RequireGrantScopeAsync(
            command.TenantId,
            grantId,
            booking.CustomerId,
            action.Value,
            booking.BookingId,
            targetType: "booking",
            // The CALLER, so a refusal names who attempted it. Without this the deny row fell back
            // to the booking's own customer — recording the victim of an enumeration sweep as the
            // party that tried, and making repeated probes by one actor impossible to correlate.
            actorId: command.ActorId,
            actorRole: command.ActorRole,
            ct: ct);

        return new AuthorizingGrant(grantId, booking.CustomerId, action.Value, booking.BookingId);
    }

    /// <summary>
    /// The assurance level of a grant that the confirmation merely NAMES (no authorization gate
    /// ran), accepted only when the grant belongs to the customer the payload names.
    ///
    /// <para>Returns <see langword="null"/> — meaning "fall back to what the caller asserted" —
    /// when the grant is unknown/expired or was issued to a different customer.</para>
    /// </summary>
    private async Task<VerificationLevel?> GetLevelForNamedGrantAsync(
        string tenantId,
        string grantId,
        string? mutationCustomerRef,
        CustomerRef? customer,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // A mutation's binding is the BOOKING's customer — authoritative, and already loaded.
        // A create has no booking, so the payload's own customer is the best available.
        var payloadCustomerRef = FirstNonBlank(
            mutationCustomerRef, customer?.CustomerId, customer?.ContactRef, customer?.PhoneE164);

        if (payloadCustomerRef is null)
        {
            // Nothing to bind against, so nothing to contradict the grant either.
            return await grants.GetAssuranceLevelAsync(tenantId, grantId, now, ct);
        }

        // Scoped read: same tenant, same customer, unexpired. Action and target are deliberately
        // NOT constrained — this asks "how well was this party verified", not "may they do this",
        // and a create has no target to scope to.
        return await grants.GetAssuranceLevelForCustomerAsync(
            tenantId, grantId, payloadCustomerRef, now, ct);
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The authorization a mutation passed, carried from the pre-check to the commit.
    ///
    /// <para><see cref="GrantId"/> is the grant to SPEND — non-null only on the grant path, since
    /// the staff-role path authorizes on the asserted role and has nothing to consume.
    /// <see cref="CustomerRef"/> is present either way: it is the binding that decides whether a
    /// grant the caller merely NAMES may supply the recorded assurance level.</para>
    /// </summary>
    private sealed record AuthorizingGrant(
        string? GrantId, string CustomerRef, VerificationActionEnum Action, string BookingId);

    /// <summary>
    /// The grant this request is authorized by: the explicit header first, then a
    /// <c>grant_</c>-prefixed confirmation identity, else none.
    ///
    /// <para>ONE resolution, used by both the authorization check and the assurance-level lookup.
    /// They were computed separately and disagreed: authorization honoured the header while the
    /// level did not, so a header-authorized request was authorized by a real grant and then
    /// recorded with a level the caller had simply asserted.</para>
    /// </summary>
    private static string ResolveGrantId(string? headerGrantId, string? confirmationIdentity)
        => !string.IsNullOrWhiteSpace(headerGrantId)
            ? headerGrantId
            : confirmationIdentity is not null
              && confirmationIdentity.StartsWith("grant_", StringComparison.Ordinal)
                ? confirmationIdentity
                : string.Empty;

    /// <summary>
    /// Runs the creator-election INSERT and, if won, the full atomic acceptance write set
    /// inside one UoW transaction. Returns <c>true</c> if THIS call won the election.
    /// </summary>
    private async Task<bool> TryElectAndWriteAsync(
        IdempotencyRecord record,
        SubmitConfirmedIntentCommand command,
        IntentRevision revision,
        Confirmation confirmation,
        OperationTypeEnum operationType,
        SubmitConfirmedIntentRequest request,
        string payloadHash,
        VerificationLevel verificationLevel,
        AuthorizingGrant? authorizingGrant,
        DateTimeOffset now,
        CancellationToken ct)
    {
        bool won = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            won = await idempotencyRepo.ElectCreatorAsync(record, ct2);
            if (!won)
                return; // concurrent loser — nothing else written in this transaction

            // Spend the grant (§9.3 / Inv-11 single-use), gated on having WON the election.
            //
            // This door validated the grant and never consumed it, while every dedicated door does
            // — so a modify/reschedule grant stayed reusable for its whole validity window, and a
            // reschedule can ONLY be submitted here (the manage-booking door carries no field for
            // the new time). The single-use property the other doors enforce was simply absent from
            // the one path that needed it most.
            //
            // Gated on the election because that is exactly the line between "the same request
            // again" and "a new one". A replay never reaches this code — it returns from the
            // pre-check or the lost-election path — so a retry stays idempotent, while a second,
            // DIFFERENT operation presenting the same spent grant lands here, affects zero rows,
            // and is denied. Inside the acceptance transaction, so a rolled-back acceptance can
            // never leave a grant spent on an operation that does not exist.
            if (authorizingGrant?.GrantId is not null)
            {
                await grantAuth.ConsumeGrantAsync(
                    command.TenantId,
                    authorizingGrant.GrantId,
                    authorizingGrant.CustomerRef,
                    authorizingGrant.Action,
                    authorizingGrant.BookingId,
                    targetType: "booking",
                    actorId: command.ActorId,
                    actorRole: command.ActorRole,
                    ct: ct2);
            }

            // Idempotent intent insert: only write the BookingIntent row if it is new.
            // A revision can arrive for an existing intent (e.g. same intent, new operation).
            if (!await intentRepo.IntentExistsAsync(command.TenantId, revision.IntentId, ct2))
            {
                await intentRepo.AddIntentAsync(new BookingIntent
                {
                    TenantId = command.TenantId,
                    IntentId = revision.IntentId,
                    OperationType = operationType,
                    SessionId = revision.SourceSessionId,
                    CreatedAt = now,
                }, ct2);
            }

            await intentRepo.AddRevisionAsync(new BookingIntentRevision
            {
                TenantId = command.TenantId,
                IntentId = revision.IntentId,
                Revision = (int)revision.Revision,
                CanonicalPayload = JsonSerializer.Serialize(revision.CanonicalPayload, Cordia.Domain.Generated.Converter.Settings),
                // BARE hash stored on the revision — the DB trigger compares
                // intent_confirmations.intent_payload_hash against this value (spec 12 §6).
                PayloadHash = payloadHash,
                SchemaVersion = revision.SchemaVersion ?? string.Empty,
                TenantPolicyVersion = revision.TenantPolicyVersion ?? string.Empty,
                PriorRevision = revision.PriorRevision.HasValue ? (int?)revision.PriorRevision.Value : null,
                SupersessionReason = revision.SupersessionReason,
                SourceSessionId = revision.SourceSessionId,
                CreatedAt = now,
            }, ct2);

            await intentRepo.AddConfirmationAsync(new IntentConfirmation
            {
                TenantId = command.TenantId,
                ConfirmationId = confirmation.ConfirmationId,
                IntentId = confirmation.IntentId,
                IntentRevision = (int)confirmation.IntentRevision,
                // BARE hash — must equal revision.payload_hash so the DB trigger passes.
                IntentPayloadHash = confirmation.IntentPayloadHash,
                IdentityRef = confirmation.Identity ?? string.Empty,
                ConfirmationChannel = ConfirmationMappings.MapConfirmationChannel(confirmation.ConfirmationChannel),
                SourceTurnId = confirmation.SourceTurnId ?? string.Empty,
                ReadbackVersion = confirmation.ReadBackVersion ?? string.Empty,
                Disclosures = JsonSerializer.Serialize(confirmation.DisclosuresPresented ?? new List<string>()),
                VerificationLevel = verificationLevel,
                ConfirmedAt = confirmation.ConfirmedAt,
            }, ct2);

            // Spec 11 §5.3 — a confirmed revision carrying a non-empty PreorderReference is a
            // pre-order: mark the operation and route it to the pre-order saga (which reserves items,
            // then delegates the whole booking — capacity/payment/create — to the 06 engine) instead
            // of straight to 06. This wires the saga's missing producer. Non-pre-order path unchanged.
            var preorder = revision.CanonicalPayload.Preorder;
            bool isPreorder = preorder is { Lines.Count: > 0 };

            // A mutation's target must be on the OPERATION row, not only inside the canonical
            // payload. Fulfilment steps are driven from the operation, so a modify/reschedule
            // whose BookingId was null quarantined on arrival at ModifyBookingCommandHandler —
            // every reschedule submitted through this front door, because only the cancellation
            // endpoint was setting it.
            var targetBookingId = revision.CanonicalPayload.TargetBookingId;

            await operationRepo.AddAsync(new BookingOperation
            {
                TenantId = command.TenantId,
                OperationId = request.OperationId,
                IntentId = revision.IntentId,
                IntentRevision = (int)revision.Revision,
                BookingId = string.IsNullOrWhiteSpace(targetBookingId) ? null : targetBookingId,
                OperationType = operationType,
                Status = OperationStatus.Accepted,
                SupersededOperationId = request.ReplacesOperationId,
                IsPreorder = isPreorder,
                // Console always sends X-Actor; the agent lane typically does not. Do not infer
                // from confirmation_channel — that is how the guest confirmed, not who submitted.
                AttributedSource = string.IsNullOrWhiteSpace(command.ActorId)
                    ? AttributedSource.Agent
                    : AttributedSource.Operator,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct2);

            // Payloads carry only non-sensitive identity/routing fields (Inv-12 — no PAN/CVV).
            var outbox = isPreorder
                ? new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = request.OperationId,
                    MessageType = PreorderSagaMessages.Start, // preorder.commands.start.v1
                    CorrelationId = request.OperationId,
                    CausationId = null,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = request.OperationId,
                        tenant_id = command.TenantId,
                        // The immutable reference copied verbatim from the confirmed revision (part of
                        // the canonical hash, Inv-1/Inv-2). Serialised with the generated converter so
                        // the saga round-trips it against the same property names.
                        preorder_reference = JsonSerializer.SerializeToNode(
                            preorder, Cordia.Domain.Generated.Converter.Settings),
                        // Booking is delegated to the 06 engine (07 capacity derived from the operation's
                        // revision). Standalone conversion (§21.4) is a separate operator path — the saga
                        // never starts standalone, so no start-time flags are carried.
                    }),
                    OccurredAt = now,
                }
                : new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = request.OperationId,
                    MessageType = "booking.command.requested.v1",
                    CorrelationId = request.OperationId,
                    CausationId = null,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = request.OperationId,
                        intent_id = revision.IntentId,
                        intent_revision = revision.Revision,
                        tenant_id = command.TenantId,
                        operation_type = operationType.ToString().ToLowerInvariant(),
                    }),
                    OccurredAt = now,
                };
            await outboxRepo.AddAsync(outbox, ct2);

            // Spec 14 §4.2 — stamp the consent state in force at acceptance onto
            // the durable acceptance audit, so the workflow can later prove
            // recording/processing was permitted for THIS submission even if the
            // session's consent row is subsequently re-recorded. One PK read
            // inside the acceptance transaction (§7 budget: negligible).
            var session = string.IsNullOrWhiteSpace(revision.SourceSessionId)
                ? null
                : await sessions.GetAsync(command.TenantId, revision.SourceSessionId, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.accepted",
                Actor = confirmation.Identity ?? string.Empty,
                SubjectRef = request.OperationId,
                CorrelationId = request.OperationId,
                OperationId = request.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    intent_id = revision.IntentId,
                    intent_revision = revision.Revision,
                    consent = session is null ? null : new
                    {
                        state = session.ConsentState?.ToString(),
                        policy_version = session.ConsentPolicyVersion,
                        jurisdiction_policy_id = session.JurisdictionPolicyId,
                        recording_allowed = session.RecordingAllowed,
                        recorded_at = session.ConsentRecordedAt,
                    },
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);
        return won;
    }

    private static AcceptanceResult BuildReplay(IdempotencyRecord record)
    {
        if (record.ResponseSnapshot is not null)
        {
            var body = JsonSerializer.Deserialize<OperationAcceptedResponse>(record.ResponseSnapshot);
            if (body is not null)
                return new Accepted(body);
        }

        // Snapshot absent (e.g. record committed as in_progress before snapshot was written):
        // reconstruct a minimal response from the durable record fields.
        return new Accepted(new OperationAcceptedResponse
        {
            OperationId = record.OperationId,
            Status = "accepted",
            StatusUrl = $"/api/booking-operations/{record.OperationId}",
            AcceptedAt = record.CreatedAt,
        });
    }
}
