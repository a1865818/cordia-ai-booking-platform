interface ConsentBadgeProps {
  /**
   * `consent_state` exactly as the session read returned it. `null` is a real answer — the
   * platform records no consent for many sessions — and is rendered as such, not hidden.
   */
  state: string | null | undefined;
  /**
   * `recording_allowed`. Shown only when consent was granted, because "recording not allowed"
   * beside "consent declined" says the same thing twice.
   */
  recordingAllowed?: boolean | null;
}

/**
 * The recorded consent state of a conversation session.
 *
 * Consent is the **guest's**, captured in-channel with recorded evidence (spec 14 §3.1). This
 * only ever displays what was recorded — there is deliberately no way to set it from here, and
 * `POST /api/conversation-sessions/{id}/consent` is one of the endpoints the console must never
 * call, precisely because an operator asserting consent on a guest's behalf is what that spec
 * forbids.
 *
 * An unrecognised value is rendered **verbatim** rather than mapped to a friendly label. The
 * vocabulary is a pg-enum that can gain members before this file knows about them, and showing
 * an unknown state as "Unknown" would hide a real answer the server gave.
 */
export function ConsentBadge({ state, recordingAllowed }: ConsentBadgeProps) {
  if (state === null || state === undefined || state === "") {
    return (
      <span className="text-[10.5px] text-t3 italic" title="The platform recorded no consent state for this session.">
        Consent not recorded
      </span>
    );
  }

  const tone = CONSENT_TONES[state] ?? UNKNOWN_TONE;
  const label = CONSENT_LABELS[state] ?? state;

  return (
    <span
      className="inline-flex items-center gap-[5px] text-[10.5px] font-semibold px-[8px] py-[3px] rounded-full whitespace-nowrap"
      style={{ background: tone.bg, color: tone.fg }}
    >
      <span className="w-[6px] h-[6px] rounded-full" style={{ background: tone.dot }} />
      {label}
      {state === "granted" && recordingAllowed === true && " · recording"}
    </span>
  );
}

/** The ratified `ConsentState` vocabulary (apps/api `Cordia.Domain.Enums.ConsentState`). */
const CONSENT_LABELS: Record<string, string> = {
  granted: "Consent granted",
  declined: "Consent declined",
  not_required: "Consent not required",
  alternative_offered: "Alternative offered",
};

const CONSENT_TONES: Record<string, { bg: string; fg: string; dot: string }> = {
  granted: { bg: '#E7F6EC', fg: '#14663A', dot: '#15924B' },
  declined: { bg: '#FDF2F2', fg: '#8A3A2F', dot: '#C0453A' },
  not_required: { bg: '#F2F2F0', fg: '#55554F', dot: '#9A9A9A' },
  alternative_offered: { bg: '#FBF3DF', fg: '#7E5606', dot: '#C98A09' },
};

/** An unmapped state is still shown — muted, so it reads as "recorded" without asserting a tone. */
const UNKNOWN_TONE = { bg: '#F2F2F0', fg: '#55554F', dot: '#9A9A9A' };
