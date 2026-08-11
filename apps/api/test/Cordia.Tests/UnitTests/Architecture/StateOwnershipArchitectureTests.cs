using System.Collections;
using System.Reflection;
using Cordia.Application.Handlers.BookingFulfillment.AdvanceOperation;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;

namespace Cordia.Tests.UnitTests.Architecture;

/// <summary>
/// Stops the sixteenth copy of a state set from being written.
///
/// <para>An audit of this codebase found fifteen places outside <c>Cordia.Domain</c> holding their
/// own collection of workflow states — terminal sets, in-progress sets, "needs attention" sets,
/// SLA partitions. None was malicious and most were reasonable in isolation; the problem is
/// cumulative. One of them had already drifted from the state machine and caused a real bug (two
/// statuses re-probed to exhaustion and then abandoned, silently). Deleting the copies fixes the
/// instances. This test addresses the CLASS, because the next copy will be added by someone who has
/// never read that story.</para>
///
/// <para><b>It is not a ban.</b> Some of these collections genuinely have to exist: EF Core cannot
/// translate a method call into SQL, so a query filter needs a materialised array. What the test
/// requires is that each one be a deliberate, justified decision recorded here — derived from its
/// state machine, or a documented delta, or a product classification that is separately proven
/// exhaustive. A new field fails until someone writes down which it is.</para>
/// </summary>
[Trait("Category", "Unit")]
public class StateOwnershipArchitectureTests
{
    /// <summary>The enums whose membership belongs to a state machine in <c>Cordia.Domain</c>.</summary>
    private static readonly Type[] StateEnums =
    [
        typeof(OperationStatus),
        typeof(NotificationState),
        typeof(WaitlistOfferStatus),
        typeof(PreorderSagaState),
        typeof(CatalogStatus),
        typeof(HoldStatus),
        typeof(BookingLifecycleStatus),
    ];

    /// <summary>
    /// Every static state collection outside <c>Cordia.Domain</c>, and WHY it is allowed to exist.
    ///
    /// <para>Key is <c>DeclaringType.FieldName</c>. The value is not decoration — it is the record
    /// that someone made a decision, and it is what a reviewer reads when the list grows.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Justified = new()
    {
        ["BookingOperationRepository.TerminalStatuses"] =
            "DERIVED from OperationStateMachine.TerminalStates. Materialised as an array because EF "
            + "translates array.Contains into SQL `= ANY` and cannot translate IsTerminal.",

        ["BookingOperationRepository.SlaEligibleStatuses"] =
            "DERIVED from OperationStateMachine.SlaEligibleStates, same SQL-translation reason.",

        ["RetentionPolicy.PrunableStatuses"] =
            "DERIVED as terminals minus Waitlisted. A DIFFERENT question from terminality — a "
            + "waitlisted operation has a live enrollment and its data must survive — and named "
            + "'prunable' so the two are not confused. Pinned by "
            + "WorkflowDefinitionTests.RetentionPrunable_IsTerminalMinusWaitlistedOnly.",

        ["RetentionPolicy.Prunable"] =
            "The O(1) membership view of PrunableStatuses, built from it.",

        ["BookingFunnelReadModel.TerminalFailureStatuses"] =
            "DERIVED from OperationStateMachine.TerminalFailureStates; array for SQL translation.",

        ["NotificationRepository.FailureStatuses"] =
            "DERIVED from OperationStateMachine.TerminalFailureStates; array for SQL translation.",

        ["NotificationRepository.AcceptedStates"] =
            "NOT a terminal/lifecycle classification: the two states that mean 'the provider took "
            + "it' for a subquery filter. Owned by the notification sweep, not the state machine.",

        ["ListOperationsQueryHandler.NeedsAttention"] =
            "PRODUCT classification — what belongs on an operator's board is a judgement, not a "
            + "state-machine fact (it includes the terminal FailedFinal). Proven exhaustive against "
            + "D1 by WorkflowDefinitionTests.NeedsAttention_ClassifiesEveryStatusDeliberately.",

        ["ListOperationsQueryHandler.DeliberatelyNotSurfaced"] =
            "The complement of NeedsAttention, existing so the pair must cover D1 exhaustively.",

        ["EvaluateOperationSlaCommandHandler.UserWaitStatuses"] =
            "A REFERENCE to OperationStateMachine.UserWaitStates, not a copy.",

        ["RecordDeliveryReceiptCommandHandler._terminalStates"] =
            "A REFERENCE to NotificationStateMachine.TerminalStates, not a copy.",

        ["MenuIngestionMonitor.InProgress"] =
            "DERIVED from CatalogStateMachine.InProgressStates; array for SQL translation.",

        ["ReconcileWaitlistAcceptCommandHandler._waitlistStopStates"] =
            "DOCUMENTED DELTA: exactly two states that are terminal for a delegated waitlist accept "
            + "but not for spec 06 (AlternativesRequired — nobody chooses on the guest's behalf; "
            + "ManualReview — an operator owns it). 'Has 06 finished' is still asked of "
            + "OperationStateMachine.IsTerminal. This is the shape a delta must take: short, named, "
            + "and reasoned — not a second terminal set.",
    };

    /// <summary>
    /// Fails when a static collection of state enum values appears outside <c>Cordia.Domain</c>
    /// without a recorded justification.
    /// </summary>
    [Fact]
    public void EveryStateCollectionOutsideTheDomainIsJustified()
    {
        var found = FindStateCollections().ToList();

        var unjustified = found
            .Where(f => !Justified.ContainsKey(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unjustified.Count == 0,
            "New state collection(s) outside Cordia.Domain:\n  "
            + string.Join("\n  ", unjustified)
            + "\n\nAsk the state machine instead of copying it. If a materialised collection is "
            + "genuinely required (EF cannot translate a method call into SQL), derive it from the "
            + "state machine and add an entry to StateOwnershipArchitectureTests.Justified saying "
            + "which of derived / documented-delta / product-classification it is.");
    }

    /// <summary>
    /// The allow-list must not outlive what it describes. A stale entry is a licence nobody is
    /// using, and it makes the list read as longer — and the problem as worse — than it is.
    /// </summary>
    [Fact]
    public void TheAllowListHasNoStaleEntries()
    {
        var found = FindStateCollections().ToHashSet(StringComparer.Ordinal);

        var stale = Justified.Keys
            .Where(k => !found.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            "Allow-list entries that no longer match any field (delete them):\n  "
            + string.Join("\n  ", stale));
    }

    private static IEnumerable<string> FindStateCollections()
    {
        // Application and Infrastructure only. Cordia.Domain is where these sets are SUPPOSED to
        // live — the state machines themselves are the authority this test protects.
        Assembly[] assemblies =
        [
            typeof(AdvanceBookingOperationCommandHandler).Assembly,
            typeof(BookingOperationRepository).Assembly,
        ];

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                // Compiler-generated closures and iterator state machines hold copies of locals;
                // they are not authored declarations and reporting them would be noise.
                if (type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                    continue;

                var fields = type.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                foreach (var field in fields)
                {
                    if (IsStateCollection(field.FieldType))
                        yield return $"{type.Name}.{field.Name}";
                }
            }
        }
    }

    /// <summary>
    /// True for a collection whose element type is one of the state enums. Deliberately structural
    /// (arrays plus anything generic over a state enum that is enumerable) rather than a list of
    /// blessed collection types — otherwise the next copy simply picks a type the check misses.
    /// </summary>
    private static bool IsStateCollection(Type type)
    {
        if (type.IsArray)
            return StateEnums.Contains(type.GetElementType());

        if (!type.IsGenericType || !typeof(IEnumerable).IsAssignableFrom(type))
            return false;

        // Only single-element-type collections. A Dictionary<State, X> is a MAPPING keyed by state,
        // not an assertion about which states are in a category, so it is a different thing.
        var args = type.GetGenericArguments();
        return args.Length == 1 && StateEnums.Contains(args[0]);
    }
}
