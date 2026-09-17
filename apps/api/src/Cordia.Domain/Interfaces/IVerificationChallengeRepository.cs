using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

public interface IVerificationChallengeRepository
{
    Task AddAsync(VerificationChallenge challenge, CancellationToken ct = default);

    Task<bool> TryAddAsync(VerificationChallenge challenge, CancellationToken ct = default);

    Task<VerificationChallenge?> GetByIdAsync(
        string tenantId,
        string verificationId,
        CancellationToken ct = default);

    Task<VerificationChallenge?> GetByIdempotencyKeyAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken ct = default);

    /// <summary>
    /// Verifies the challenge atomically: only if it is unverified, unexpired, and has fewer than
    /// <paramref name="maxFailedAttempts"/> failed attempts. Returns rows affected.
    /// </summary>
    Task<int> MarkVerifiedAsync(
        string tenantId,
        string verificationId,
        string grantId,
        DateTimeOffset verifiedAt,
        DateTimeOffset grantExpiresAt,
        int maxFailedAttempts,
        CancellationToken ct = default);

    /// <summary>
    /// Records one wrong code atomically, and only while the challenge is unverified and below
    /// <paramref name="maxFailedAttempts"/>, so concurrent guesses cannot push past the limit.
    /// Returns rows affected. Zero means no eligible row matched: the challenge is locked, already
    /// verified, does not exist, or belongs to another tenant — callers must not read it as "locked".
    /// </summary>
    Task<int> RecordFailedAttemptAsync(
        string tenantId,
        string verificationId,
        int maxFailedAttempts,
        CancellationToken ct = default);
}
