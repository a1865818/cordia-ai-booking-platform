using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Application.Handlers.BookingFulfillment;

/// <summary>
/// Builds the <c>bookings</c> row that MUST accompany a <c>Booked</c> operation, and the payload
/// reader its identity resolution depends on.
///
/// <para><b>Why this is a shared factory and not a private helper (U1, #59).</b> There are four
/// writers of the <c>Booked</c> outcome — <see cref="Finalize.FinalizeBookingCommandHandler"/>'s
/// main path and its recovery shortcut, <see cref="Reconcile.ReconcileOperationCommandHandler"/>,
/// and <see cref="ManualRecovery.ResolveManualReviewCommandHandler"/> — and until now only the
/// two inside Finalize could materialise a booking row, because this code was private to that
/// class. An operation that reached <c>Booked</c> through reconciliation therefore had no
/// <c>bookings</c> row at all, which makes it invisible to <c>GetBookingDetailsQuery</c>, to the
/// console, and to the cancel and modify front doors.</para>
///
/// <para><b>Moved, never copied.</b> The obvious alternative — a second copy in the reconcile
/// handler — would put two copies of the <c>CustomerId</c> precedence chain in the tree. That
/// value is the identity <c>AuthorizeMutationAsync</c> later matches a verification grant
/// against, so the two copies drifting apart would lock guests out of their own bookings on
/// whichever path fell behind. Spec 06 §19.1 warns about exactly this shape. The code below is
/// byte-for-byte what lived in <c>FinalizeBookingCommandHandler</c>, and the tests that pinned it
/// there now pin it here with their assertions unchanged.</para>
/// </summary>
internal static class BookingAggregateFactory
{
    internal static Booking BuildBookingAggregate(
        string tenantId,
        BookingOperation operation,
        string providerReference,
        string canonicalPayload,
        DateTimeOffset now)
    {
        var bookingReference = ReadFirstString(canonicalPayload, "booking_reference", "bookingReference", "reference")
            ?? providerReference;
        // `contact_ref` ranks directly behind an explicit customer_id and AHEAD of the legacy
        // value-carrying keys. Under the out-of-band contact shape the payload deliberately holds
        // no phone or email, so without it every guest-bearing booking falls through to
        // `?? providerReference` — which then becomes the CustomerId that AuthorizeMutationAsync
        // compares a verification grant against, locking the guest out of their own booking, and
        // the opaque id the console renders where the guest should be. Legacy payloads carry no
        // contact_ref, so their resolution is unchanged.
        var customerRef = ReadFirstString(
                canonicalPayload,
                "customer_id",
                "customerId",
                "contact_ref",
                "contactRef",
                "caller_id",
                "callerId",
                "phone_e164",
                "phoneE164",
                "contact_phone",
                "contactPhone",
                "email",
                "contact_email",
                "contactEmail")
            ?? providerReference;

        return new Booking
        {
            TenantId = tenantId,
            BookingId = providerReference,
            BookingReference = bookingReference,
            ProviderReference = providerReference,
            Version = 0,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerRef,
            AttributedSource = operation.AttributedSource,
            CreatedAt = operation.CreatedAt,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The first of <paramref name="propertyNames"/> that the payload actually carries, in the
    /// order the CALLER listed them.
    ///
    /// <para>That order is the point. Every call site here passes a deliberate preference chain —
    /// an explicit <c>customer_id</c> outranks a <c>contact_ref</c>, which outranks the legacy
    /// value-carrying keys — but the search used to walk the DOCUMENT and return whichever listed
    /// name it happened to meet first, descending into a property's whole subtree before even
    /// looking at the next top-level key. The stated precedence held only because current producers
    /// happen to emit keys in a convenient order; a nested <c>phone_e164</c> under an earlier
    /// property would beat a top-level <c>customer_id</c>, and the resulting value becomes the
    /// booking's <c>CustomerId</c> — the identity a verification grant is later matched against.
    /// One name at a time makes the precedence real instead of incidental.</para>
    /// </summary>
    internal static string? ReadFirstString(string json, params string[] propertyNames)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in propertyNames)
            {
                var value = FindNamed(doc.RootElement, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Searches the document for ONE property name, shallowest match first.
    ///
    /// <para>A true level-order walk, not a depth-first descent with the current level checked
    /// early. Depth-first meant a match buried under an earlier property still beat a shallow one
    /// under a later property — so for
    /// <c>{"preorder":{"items":[{"meta":{"customer_id":"stale"}}]},"customer":{"customer_id":"real"}}</c>
    /// the whole <c>preorder</c> subtree was exhausted first and "stale" won. That value becomes
    /// the booking's <c>CustomerId</c>, which a verification grant is later matched against —
    /// locking the real guest out of their own booking.</para>
    /// </summary>
    private static string? FindNamed(JsonElement root, string propertyName)
    {
        var queue = new Queue<JsonElement>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var element = queue.Dequeue();

            if (element.ValueKind == JsonValueKind.Object)
            {
                // Every key at this depth before anything deeper.
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        var value = StringValue(property.Value);
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        queue.Enqueue(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        queue.Enqueue(item);
                }
            }
        }

        return null;
    }

    private static string? StringValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
}
