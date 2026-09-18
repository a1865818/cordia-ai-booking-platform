using System.Globalization;
using Cordia.Application.Handlers.Tenancy.GetOperatingHours;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingDrafts.ValidateBookingDraft;

/// <summary>
/// Judges a draft against the two tenant rules a phone booking can break before anyone
/// looks at capacity: how many people, and whether the venue is open.
///
/// <para><b>Only concrete values are judged.</b> A missing or TriState field is not a policy
/// failure - it is the capture loop's job, and reporting it here would have the agent answer
/// "how many of you will there be?" with a policy message instead of the question the
/// clarification ladder was about to ask. So an absent field is simply not checked.</para>
///
/// <para><b>Config gaps fail OPEN.</b> No timezone, no parseable hours row, an IANA id this
/// machine does not know: in every one of those the venue's wall clock is unknowable, and a
/// rule evaluated against an unknown clock would refuse real bookings at the venue's busiest
/// hour. A genuine violation fails closed; a broken configuration does not.</para>
/// </summary>
public sealed class ValidateBookingDraftQueryHandler(ITenantProfileRepository profiles)
    : IRequestHandler<ValidateBookingDraftQuery, ValidateBookingDraftResult>
{
    /// <summary>
    /// Largest party the agent may take unaided.
    ///
    /// Twenty, because that is where a restaurant booking stops being a table and starts
    /// being an event: deposits, a set menu, a room. The number is a policy default rather
    /// than a physical limit, and it lives here as one named constant so raising it for a
    /// tenant later means adding a column, not hunting for a literal.
    /// </summary>
    public const int MaxPartySize = 20;

    /// <summary>Smallest party that means anything. Zero people is not a booking.</summary>
    public const int MinPartySize = 1;

    /// <summary>Reported when the draft carries no policy version of its own.</summary>
    public const string UnknownPolicyVersion = "unknown";

    /// <summary>Machine hint telling the agent to ask for a different time.</summary>
    private const string RequestAlternativeTime = "request_alternative_time";

    /// <summary>Machine hint telling the agent to hand the group to a person.</summary>
    private const string RequestHumanHandoff = "request_human_handoff";

    public async Task<ValidateBookingDraftResult> Handle(ValidateBookingDraftQuery request, CancellationToken ct)
    {
        var draft = request.Draft.Request;
        var policyVersion = string.IsNullOrWhiteSpace(request.Draft.TenantPolicyVersion)
            ? UnknownPolicyVersion
            : request.Draft.TenantPolicyVersion;

        var partySize = draft.PartySize?.Integer;
        if (partySize is { } size && (size > MaxPartySize || size < MinPartySize))
        {
            return Invalid(
                ValidateBookingDraftStatusEnum.PartySizeUnsupported,
                size > MaxPartySize
                    ? $"That's a big group! For anything over {MaxPartySize} the team will need to sort it out with you directly, so let me put you through."
                    : "Sorry, how many of you will there be?",
                ["party_size"],
                size > MaxPartySize ? RequestHumanHandoff : null,
                policyVersion);
        }

        var startsAt = draft.StartsAt?.DateTime;
        if (startsAt is null)
            return Valid(policyVersion);

        var profile = await profiles.GetAsync(request.TenantId, ct);

        // A time that has already been and gone is the one clock error the agent makes on its
        // own: "Friday at seven" resolved against last Friday reads as a perfectly valid draft.
        if (startsAt.Value <= DateTimeOffset.UtcNow)
        {
            return Invalid(
                ValidateBookingDraftStatusEnum.MinimumNoticeNotMet,
                "That one's already been and gone, sorry. What day were you thinking?",
                ["starts_at"],
                RequestAlternativeTime,
                policyVersion);
        }

        var zone = ResolveZone(profile?.Timezone);
        if (zone is null)
            return Valid(policyVersion);

        var hours = OperatingHoursDefaults.ParseOrDefault(profile?.HoursJson);
        var local = TimeZoneInfo.ConvertTime(startsAt.Value, zone);
        var day = DayFor(hours, local.DayOfWeek);

        if (day is null || !TryWindow(day, out var open, out var close))
            return Valid(policyVersion);

        if (!day.On)
        {
            return Invalid(
                ValidateBookingDraftStatusEnum.OutsideOperatingHours,
                $"We're closed {day.D}s, sorry. {OpenSummary(hours)} Would another day work?",
                ["starts_at"],
                RequestAlternativeTime,
                policyVersion);
        }

        if (!IsWithin(TimeOnly.FromDateTime(local.DateTime), open, close))
        {
            return Invalid(
                ValidateBookingDraftStatusEnum.OutsideOperatingHours,
                $"We're not open then, sorry. On {day.D}s we're open {Spoken(open)} till {Spoken(close)}. Would another time suit?",
                ["starts_at"],
                RequestAlternativeTime,
                policyVersion);
        }

        return Valid(policyVersion);
    }

    /// <summary>
    /// Returns the venue's zone, or <c>null</c> when it cannot be resolved.
    ///
    /// <c>null</c> deliberately skips the opening-hours rule rather than falling back to UTC:
    /// UTC is ten hours out from the venue this was built for, so the fallback would reject
    /// the dinner sitting and accept 5am.
    /// </summary>
    private static TimeZoneInfo? ResolveZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static OperatingHoursDay? DayFor(IReadOnlyList<OperatingHoursDay> hours, DayOfWeek dayOfWeek)
        => hours.FirstOrDefault(d => string.Equals(d.D, dayOfWeek.ToString(), StringComparison.OrdinalIgnoreCase));

    private static bool TryWindow(OperatingHoursDay day, out TimeOnly open, out TimeOnly close)
        => TimeOnly.TryParseExact(day.Open, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out open)
           & TimeOnly.TryParseExact(day.Close, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out close);

    /// <summary>
    /// Whether <paramref name="at"/> falls in the window, including windows that run past
    /// midnight (a bar closing at 02:00 has <c>close &lt;= open</c>, and a naive comparison
    /// would call every one of its opening hours closed).
    /// </summary>
    private static bool IsWithin(TimeOnly at, TimeOnly open, TimeOnly close)
        => close <= open ? at >= open || at < close : at >= open && at < close;

    /// <summary>
    /// "We're open Tuesday to Sunday, 8am till 10pm." - said when the caller has picked a day
    /// the venue is shut, so the next thing they say can be a day that works.
    ///
    /// The hours are only quoted when every open day shares them; a venue with a short Sunday
    /// would otherwise be told to the caller wrong, which is worse than being told less.
    /// </summary>
    private static string OpenSummary(IReadOnlyList<OperatingHoursDay> hours)
    {
        var open = hours.Where(d => d.On).ToList();
        if (open.Count == 0)
            return "I can't see any opening times for us, sorry.";

        var days = DayPhrase(hours, open);
        var uniform = open.All(d => d.Open == open[0].Open && d.Close == open[0].Close);

        if (!uniform || !TryWindow(open[0], out var opensAt, out var closesAt))
            return $"We're open {days}.";

        return $"We're open {days}, {Spoken(opensAt)} till {Spoken(closesAt)}.";
    }

    /// <summary>
    /// "every day", "Tuesday to Sunday", "Mondays", or a plain list - whichever is true.
    /// A run is only read as a run when the open days are contiguous in Monday-first order,
    /// so a venue shut on Wednesday is not announced as open "Monday to Sunday".
    /// </summary>
    private static string DayPhrase(IReadOnlyList<OperatingHoursDay> hours, IReadOnlyList<OperatingHoursDay> open)
    {
        if (open.Count == hours.Count)
            return "every day";
        if (open.Count == 1)
            return $"{open[0].D}s";

        var indexes = open.Select(d => IndexOfDay(hours, d.D)).ToList();
        var contiguous = indexes.Zip(indexes.Skip(1), (a, b) => b == a + 1).All(x => x);
        if (contiguous)
            return $"{open[0].D} to {open[^1].D}";

        var names = open.Select(d => d.D).ToList();
        return string.Join(", ", names[..^1]) + $", and {names[^1]}";
    }

    private static int IndexOfDay(IReadOnlyList<OperatingHoursDay> hours, string day)
    {
        for (var i = 0; i < hours.Count; i++)
        {
            if (string.Equals(hours[i].D, day, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Renders a time the way a person says it down a phone: "8am", "10pm", "8:30am".
    /// A TTS voice reading "22:00" says "twenty two hundred", which nobody booking a table has
    /// ever heard.
    /// </summary>
    private static string Spoken(TimeOnly time)
    {
        var hour = time.Hour % 12 == 0 ? 12 : time.Hour % 12;
        var suffix = time.Hour < 12 ? "am" : "pm";
        return time.Minute == 0
            ? $"{hour}{suffix}"
            : $"{hour}:{time.Minute:00}{suffix}";
    }

    private static ValidateBookingDraftResult Valid(string policyVersion) =>
        new(ValidateBookingDraftStatusEnum.Valid, new PolicyValidationResult
        {
            Valid = true,
            Code = ValidateBookingDraftStatusEnum.Valid,
            CustomerMessage = string.Empty,
            RecoverableFields = [],
            SuggestedAction = null!,
            PolicyVersion = policyVersion,
        });

    private static ValidateBookingDraftResult Invalid(
        ValidateBookingDraftStatusEnum code,
        string customerMessage,
        List<string> recoverableFields,
        string? suggestedAction,
        string policyVersion) =>
        new(code, new PolicyValidationResult
        {
            Valid = false,
            Code = code,
            CustomerMessage = customerMessage,
            RecoverableFields = recoverableFields,
            SuggestedAction = suggestedAction!,
            PolicyVersion = policyVersion,
        });
}
