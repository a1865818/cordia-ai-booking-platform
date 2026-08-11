interface CountdownProps {
  /** The expiry instant as the server stated it. Absent renders nothing at all. */
  expiresAt: string | null | undefined;
  /**
   * The current time, in ms.
   *
   * **Required, and deliberately not read from a hook inside this component.** A list renders
   * one Countdown per row; if each owned its own interval they would tick out of phase with each
   * other *and* with whatever the screen uses to decide which actions are still legal — so a row
   * could show "expired" beside a live Accept button. One clock per screen (`useTickingNow`),
   * passed down, keeps the label and the button provably in agreement.
   */
  now: number;
  /** Rendered when the deadline has passed. */
  expiredLabel?: string;
  /**
   * Rendered immediately before the label, and **only when there is one**. Exists so a caller
   * can write a separator without having to re-derive whether a label will appear — getting
   * that wrong leaves a dangling "·" whenever the instant is absent or unparseable.
   */
  prefix?: string;
}

/**
 * A live time-to-expiry label for holds, offers and links.
 *
 * Always **floors**, never rounds. Rounding to the nearest minute is what once made this
 * disagree with the actionability check: an offer 25 seconds from expiry was still actionable
 * while its label already read "expired". Flooring means the label can understate the time left
 * but never overstate it, so it can never promise time the platform will not honour.
 */
export function Countdown({ expiresAt, now, expiredLabel = "expired", prefix }: CountdownProps) {
  const label = remainingLabel(expiresAt, now, expiredLabel);
  if (label === "") return null;

  return (
    <span role="timer" aria-live="off">
      {prefix}{label}
    </span>
  );
}

/**
 * The label rule on its own, for callers that need the string.
 *
 * Exported so there is exactly ONE implementation of "how long is left" in the console — the
 * waitlist's `offerExpiryLabel` delegates here rather than keeping a second copy that could
 * drift from this one.
 *
 * Returns `""` for an absent or unparseable instant: no deadline is a real state, and inventing
 * "expired" for it would report an expiry the server never stated.
 */
export function remainingLabel(
  expiresAt: string | null | undefined,
  now: number,
  expiredLabel = "expired"
): string {
  if (!expiresAt) return "";

  const expiry = new Date(expiresAt).getTime();
  if (Number.isNaN(expiry)) return "";
  if (expiry <= now) return expiredLabel;

  const minutes = Math.floor((expiry - now) / 60_000);
  if (minutes < 1) return "expires in under a minute";
  if (minutes < 60) return `expires in ${minutes} min`;
  return `expires in ${Math.floor(minutes / 60)} hr`;
}
