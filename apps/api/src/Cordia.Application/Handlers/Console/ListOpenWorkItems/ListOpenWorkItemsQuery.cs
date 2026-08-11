using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.ListOpenWorkItems;

/// <summary>
/// The outstanding manual-recovery queue for one tenant (spec 06 §17.5, FM#11).
///
/// <para><b>Why this query exists.</b> Every path that gives up on an operation — an ambiguous
/// provider cancel, an exhausted retry budget, a poison message, an SLA breach, a park of a
/// committed pre-order — durably records an <c>operations_work_items</c> row with a reason and an
/// SLA deadline. Nothing could read them. The table had exactly two aggregate readers, both
/// cross-tenant and both feeding metrics gauges, so a dashboard could tell an operator that the
/// manual-review depth was climbing without being able to tell them what was in it. The escalation
/// was durable and unreachable at the same time.</para>
///
/// <para>Ordered by SLA deadline — the most overdue first — because that is the order the work
/// should be done in, and different reasons carry different deadlines, so creation order is not the
/// same thing.</para>
/// </summary>
public sealed record ListOpenWorkItemsQuery(
    string TenantId,
    int Limit) : IRequest<IReadOnlyList<OperationWorkItemSummary>>;

public sealed class ListOpenWorkItemsValidator : AbstractValidator<ListOpenWorkItemsQuery>
{
    public const int MaxLimit = 500;

    public ListOpenWorkItemsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxLimit)
            .WithMessage($"limit must be between 1 and {MaxLimit}.");
    }
}

public sealed class ListOpenWorkItemsQueryHandler(
    IOperationWorkItemRepository workItems,
    IBookingOperationRepository operations)
    : IRequestHandler<ListOpenWorkItemsQuery, IReadOnlyList<OperationWorkItemSummary>>
{
    public async Task<IReadOnlyList<OperationWorkItemSummary>> Handle(
        ListOpenWorkItemsQuery request, CancellationToken ct)
    {
        var open = await workItems.ListOpenAsync(request.TenantId, request.Limit, ct);

        var summaries = new List<OperationWorkItemSummary>(open.Count);

        foreach (var item in open)
        {
            // The operation's CURRENT status, not the one it had when the item was opened.
            //
            // An operator needs to know whether this is still actionable: a work item opened by the
            // SLA sweeper for a slow operation stays open until someone closes it, but the operation
            // underneath may have finished on its own in the meantime. Showing the stale reason
            // alone would send someone to look at a booking that no longer needs them.
            var op = await operations.GetAsync(item.OperationId, ct);

            summaries.Add(new OperationWorkItemSummary(
                item.WorkItemId,
                item.OperationId,
                item.Reason,
                item.SlaDeadline,
                item.Assignee,
                item.CreatedAt,
                // Cross-tenant defence: the repository query is already tenant-scoped, but
                // GetAsync resolves by operation id alone, so the status is only surfaced when the
                // row genuinely belongs to this tenant (Inv-13).
                op is not null && op.TenantId == request.TenantId ? op.Status : null));
        }

        return summaries;
    }
}
