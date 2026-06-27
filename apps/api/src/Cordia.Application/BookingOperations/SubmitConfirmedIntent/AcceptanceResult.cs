using Cordia.Domain.Generated;

namespace Cordia.Application.BookingOperations.SubmitConfirmedIntent;

/// <summary>
/// Discriminated union returned by <see cref="SubmitConfirmedIntentCommandHandler"/> (spec 04 §4.3).
/// The controller maps each case to its HTTP status code:
///   <see cref="Accepted"/>  → 202 (new acceptance or idempotent replay)
///   <see cref="Conflict"/>  → 409 (same key bound to a different request hash, Inv-3)
/// Validation/policy/hash failures throw typed domain exceptions (→ 422 via GlobalExceptionHandler).
/// </summary>
public abstract record AcceptanceResult;

/// <summary>The operation was durably accepted (new) or replayed from the idempotency store.</summary>
public sealed record Accepted(OperationAcceptedResponse Body) : AcceptanceResult;

/// <summary>The idempotency key is already bound to a different request hash (Inv-3, §5.1).</summary>
public sealed record Conflict(IdempotencyConflict Detail) : AcceptanceResult;
