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

    Task<int> MarkVerifiedAsync(
        string tenantId,
        string verificationId,
        string grantId,
        DateTimeOffset verifiedAt,
        DateTimeOffset grantExpiresAt,
        CancellationToken ct = default);
}
