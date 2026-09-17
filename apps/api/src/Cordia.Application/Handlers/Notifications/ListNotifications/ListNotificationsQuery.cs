using System.Text.Json.Serialization;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Notifications.ListNotifications;

/// <summary>
/// Delivery state of the notifications raised for a booking or an operation (#83, spec 10).
/// Exactly one of <see cref="BookingId"/> and <see cref="OperationId"/> is set by the controller.
/// </summary>
public record ListNotificationsQuery(string TenantId, string? BookingId, string? OperationId)
    : IRequest<ListNotificationsResponse>;

/// <summary>
/// One notification as the console may show it: what was sent, over which channel, and what the
/// provider last said about it.
///
/// <para>No recipient, provider reference or idempotency key. The operator is asking "did the guest
/// hear from us"; the destination is contact PII (Inv-12) and the provider identifiers are for
/// reconciliation, not for display.</para>
/// </summary>
public record NotificationDeliveryView(
    [property: JsonPropertyName("notification_id")] string NotificationId,
    [property: JsonPropertyName("operation_id")] string? OperationId,
    [property: JsonPropertyName("event_type")] NotificationEvent EventType,
    [property: JsonPropertyName("channel")] NotificationChannel Channel,
    [property: JsonPropertyName("state")] NotificationState State,
    [property: JsonPropertyName("failure_category")] string? FailureCategory,
    [property: JsonPropertyName("attempt_count")] int AttemptCount,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public record ListNotificationsResponse(
    [property: JsonPropertyName("notifications")] IReadOnlyList<NotificationDeliveryView> Notifications);

public sealed class ListNotificationsQueryHandler(INotificationRepository notifications)
    : IRequestHandler<ListNotificationsQuery, ListNotificationsResponse>
{
    /// <summary>
    /// A booking accrues a handful of rows (request accepted, confirmed, a change or two). The cap
    /// exists so a pathological retry storm cannot turn a drawer open into an unbounded read.
    /// </summary>
    internal const int Limit = 100;

    public async Task<ListNotificationsResponse> Handle(ListNotificationsQuery request, CancellationToken ct)
    {
        var rows = request.BookingId is not null
            ? await notifications.ListForBookingAsync(request.TenantId, request.BookingId, Limit, ct)
            : await notifications.ListForOperationAsync(request.TenantId, request.OperationId!, Limit, ct);

        // Rows are returned exactly as stored. A booking with no notification row yields an empty
        // list — never a synthesised "sent" (#83).
        return new ListNotificationsResponse(rows
            .Select(n => new NotificationDeliveryView(
                n.NotificationId,
                n.OperationId,
                n.EventType,
                n.Channel,
                n.State,
                n.FailureCategory,
                n.AttemptCount,
                n.CreatedAt,
                n.UpdatedAt))
            .ToList());
    }
}
