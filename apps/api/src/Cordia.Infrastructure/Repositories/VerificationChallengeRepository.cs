using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Infrastructure.Repositories;

public class VerificationChallengeRepository(ApplicationDbContext db) : IVerificationChallengeRepository
{
    public async Task AddAsync(VerificationChallenge challenge, CancellationToken ct = default)
    {
        db.VerificationChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryAddAsync(VerificationChallenge challenge, CancellationToken ct = default)
    {
        db.VerificationChallenges.Add(challenge);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(challenge).State = EntityState.Detached;
            return false;
        }
    }

    public Task<VerificationChallenge?> GetByIdAsync(
        string tenantId,
        string verificationId,
        CancellationToken ct = default)
        => db.VerificationChallenges
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.VerificationId == verificationId, ct);

    public Task<VerificationChallenge?> GetByIdempotencyKeyAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken ct = default)
        => db.VerificationChallenges
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.IdempotencyKey == idempotencyKey, ct);

    public Task<int> MarkVerifiedAsync(
        string tenantId,
        string verificationId,
        string grantId,
        DateTimeOffset verifiedAt,
        DateTimeOffset grantExpiresAt,
        int maxFailedAttempts,
        CancellationToken ct = default)
        => db.VerificationChallenges
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.VerificationId == verificationId
                        && c.VerifiedAt == null
                        && verifiedAt < c.ExpiresAt
                        // Re-asserted here, not only read before: a guess that locks the
                        // challenge concurrently must win over a right code still in flight.
                        && c.FailedAttempts < maxFailedAttempts)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(c => c.VerifiedAt, verifiedAt)
                    .SetProperty(c => c.GrantId, grantId)
                    .SetProperty(c => c.ExpiresAt, grantExpiresAt),
                ct);

    public Task<int> RecordFailedAttemptAsync(
        string tenantId,
        string verificationId,
        int maxFailedAttempts,
        CancellationToken ct = default)
        => db.VerificationChallenges
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.VerificationId == verificationId
                        && c.VerifiedAt == null
                        && c.FailedAttempts < maxFailedAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedAttempts, c => c.FailedAttempts + 1), ct);
}
