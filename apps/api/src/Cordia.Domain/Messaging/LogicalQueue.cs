namespace Cordia.Domain.Messaging;

/// <summary>
/// The 8 logical queues in the Cordia messaging topology (§3, §10, §12.1).
/// Physical consolidation is allowed; these contracts stay distinct.
/// This enum is NOT a PostgreSQL enum — no [PgName]/[JsonStringEnumMemberName] attributes.
/// </summary>
public enum LogicalQueue
{
    BookingCommands,
    BookingReconcile,
    BookingScheduled,
    BookingCompensate,
    NotificationCommands,
    WaitlistCommands,
    MenuCommands,
    OperationsReview,
}

/// <summary>
/// Metadata for a single logical queue: wire name, ordering requirement, and consumer set (§12.1).
/// </summary>
public sealed record LogicalQueueMetadata(
    LogicalQueue Queue,
    string Name,
    bool OrderingRequired,
    IReadOnlyList<string> Consumers);

/// <summary>
/// Registry of all logical queues with their metadata and message-type routing (§10, §12.1).
/// </summary>
public static class LogicalQueueRegistry
{
    // Ordered prefix table: most-specific first so longest match wins.
    private static readonly (string Prefix, LogicalQueue Queue)[] _routingTable =
    [
        ("booking.reconcile.",   LogicalQueue.BookingReconcile),
        ("booking.scheduled.",   LogicalQueue.BookingScheduled),
        ("booking.compensate.",  LogicalQueue.BookingCompensate),
        ("booking.",             LogicalQueue.BookingCommands),
        ("notification.",        LogicalQueue.NotificationCommands),
        ("waitlist.",            LogicalQueue.WaitlistCommands),
        ("menu.",                LogicalQueue.MenuCommands),
        ("operations.",          LogicalQueue.OperationsReview),
    ];

    /// <summary>All 8 logical queue metadata entries.</summary>
    public static readonly IReadOnlyList<LogicalQueueMetadata> All =
    [
        new(LogicalQueue.BookingCommands,     "booking.commands",     OrderingRequired: true,  ["booking.finalizer", "booking.manage"]),
        new(LogicalQueue.BookingReconcile,    "booking.reconcile",    OrderingRequired: true,  ["booking.reconciler"]),
        new(LogicalQueue.BookingScheduled,    "booking.scheduled",    OrderingRequired: false, ["booking.scheduler"]),
        new(LogicalQueue.BookingCompensate,   "booking.compensate",   OrderingRequired: false, ["booking.compensator"]),
        new(LogicalQueue.NotificationCommands,"notification.commands",OrderingRequired: false, ["notification.sender"]),
        new(LogicalQueue.WaitlistCommands,    "waitlist.commands",    OrderingRequired: false, ["waitlist.processor"]),
        new(LogicalQueue.MenuCommands,        "menu.commands",        OrderingRequired: false, ["menu.processor"]),
        new(LogicalQueue.OperationsReview,    "operations.review",    OrderingRequired: false, ["operations.reviewer"]),
    ];

    private static readonly Dictionary<LogicalQueue, LogicalQueueMetadata> _byQueue =
        All.ToDictionary(m => m.Queue);

    private static readonly Dictionary<string, LogicalQueue> _byWireName =
        All.ToDictionary(m => m.Name, m => m.Queue, StringComparer.Ordinal);

    /// <summary>Returns the metadata for <paramref name="queue"/>.</summary>
    public static LogicalQueueMetadata Get(LogicalQueue queue) => _byQueue[queue];

    /// <summary>Returns the wire name for <paramref name="queue"/> (e.g. <c>"booking.commands"</c>).</summary>
    public static string WireName(LogicalQueue queue) => _byQueue[queue].Name;

    /// <summary>
    /// Resolves a wire name (e.g. <c>"booking.scheduled"</c>) back to its <see cref="LogicalQueue"/>.
    /// Returns <see langword="true"/> and sets <paramref name="queue"/> on success;
    /// returns <see langword="false"/> for an unknown wire name.
    /// Used by the scheduled-job poller to map <c>DestinationQueue</c> back to the enum (T5).
    /// </summary>
    public static bool TryGetByWireName(string wireName, out LogicalQueue queue) =>
        _byWireName.TryGetValue(wireName, out queue);

    /// <summary>
    /// Attempts to resolve a <paramref name="messageType"/> to its destination <see cref="LogicalQueue"/>
    /// using longest-prefix matching (§10). Returns <see langword="true"/> and sets <paramref name="queue"/>
    /// on success; returns <see langword="false"/> if no prefix matches.
    /// </summary>
    public static bool TryResolve(string messageType, out LogicalQueue queue)
    {
        foreach (var (prefix, candidate) in _routingTable)
        {
            if (messageType.StartsWith(prefix, StringComparison.Ordinal))
            {
                queue = candidate;
                return true;
            }
        }

        queue = default;
        return false;
    }

    /// <summary>
    /// Resolves a <paramref name="messageType"/> to its destination <see cref="LogicalQueue"/>.
    /// Throws <see cref="ArgumentException"/> if the type cannot be routed.
    /// </summary>
    public static LogicalQueue Resolve(string messageType)
    {
        if (TryResolve(messageType, out var queue))
            return queue;

        throw new ArgumentException(
            $"Message type '{messageType}' does not match any known logical queue prefix.",
            nameof(messageType));
    }
}
