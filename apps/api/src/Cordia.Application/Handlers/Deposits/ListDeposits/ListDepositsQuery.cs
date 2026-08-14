using System.Text.Json.Serialization;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Deposits.ListDeposits;

/// <summary>
/// Tenant-scoped deposit-session directory (docs/backend-gaps.md G2).
/// Filter <c>status</c> is the console vocabulary (held/captured/failed/refunded), not the
/// stored <see cref="DepositStatus"/> members.
/// </summary>
public sealed record ListDepositsQuery(
    string TenantId,
    string? Status,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit) : IRequest<DepositListResult>;

public sealed record DepositListResult(
    [property: JsonPropertyName("deposits")] IReadOnlyList<DepositDto> Deposits);

public sealed record DepositDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("amount_minor")] int AmountMinor,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("paid_at")] DateTimeOffset? PaidAt,
    [property: JsonPropertyName("service_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ServiceId = null);

/// <summary>Maps console filter tokens onto stored <see cref="DepositStatus"/> values.</summary>
public static class DepositListStatus
{
    public static readonly IReadOnlyDictionary<string, DepositStatus[]> Map =
        new Dictionary<string, DepositStatus[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["held"] = [DepositStatus.Created, DepositStatus.LinkSent],
            ["captured"] = [DepositStatus.Paid],
            ["failed"] = [DepositStatus.Expired],
            ["refunded"] = [DepositStatus.Refunded],
        };

    public static bool IsAllowed(string? status)
        => string.IsNullOrWhiteSpace(status) || Map.ContainsKey(status.Trim());

    public static DepositStatus[]? Resolve(string? status)
        => string.IsNullOrWhiteSpace(status) ? null : Map[status.Trim()];
}

public sealed class ListDepositsValidator : AbstractValidator<ListDepositsQuery>
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public ListDepositsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Limit).InclusiveBetween(1, MaxLimit);
        RuleFor(x => x.Status)
            .Must(DepositListStatus.IsAllowed)
            .WithMessage("status must be held, captured, failed, or refunded.");
    }
}

public sealed class ListDepositsQueryHandler(IDepositSessionRepository repo)
    : IRequestHandler<ListDepositsQuery, DepositListResult>
{
    public async Task<DepositListResult> Handle(ListDepositsQuery request, CancellationToken ct)
    {
        var statuses = DepositListStatus.Resolve(request.Status);
        IReadOnlyList<DepositSession> rows;

        if (statuses is null)
        {
            rows = await repo.ListAsync(
                request.TenantId, null, request.From, request.To, request.Limit, ct);
        }
        else if (statuses.Length == 1)
        {
            rows = await repo.ListAsync(
                request.TenantId, statuses[0], request.From, request.To, request.Limit, ct);
        }
        else
        {
            var merged = new List<DepositSession>();
            foreach (var status in statuses)
            {
                merged.AddRange(await repo.ListAsync(
                    request.TenantId, status, request.From, request.To, request.Limit, ct));
            }

            rows = merged
                .OrderByDescending(d => d.CreatedAt)
                .Take(request.Limit)
                .ToList();
        }

        // service_id lives on the intent revision's canonical payload (booking_operations →
        // intent revision). That join is per-row and not on IDepositSessionRepository, so it
        // is omitted; the console can label from id.
        return new DepositListResult([.. rows.Select(ToDto)]);
    }

    private static DepositDto ToDto(DepositSession s) => new(
        s.DepositSessionId,
        s.OperationId,
        s.AmountMinor,
        s.Currency,
        EnumWireNames.ToWireName(s.Status),
        s.CreatedAt,
        s.PaidAt);
}
