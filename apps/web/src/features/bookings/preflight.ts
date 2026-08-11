/**
 * The reschedule pre-flight (`POST /api/Bookings/{id}/changes:validate`) and its refusal wording.
 *
 * Extracted from `ConsolePage` so the refusal mapping is testable without rendering the console.
 * That mapping is the part most likely to drift: it enumerates the server's
 * `ValidateBookingChangeStatusEnum`, and the vocabulary grows on the server's schedule, not the
 * console's.
 */

import { validateChange } from "./submit";

/**
 * Runs the pre-flight and turns a refusal into operator language.
 *
 * Returns `null` when the change may proceed — INCLUDING when the pre-flight itself could not be
 * reached. This read holds nothing and decides nothing; the acceptance door re-checks version,
 * capacity and policy authoritatively. Treating an unreachable advisory read as a refusal would
 * let an outage on it take down a mutation that would have succeeded, which is the opposite of
 * what a "prevent the error" check is for.
 *
 * What it actually catches today is `stale_version` — the booking moved underneath the grid
 * between the read and the submit. `ValidateBookingChangeQueryHandler` takes only a booking
 * repository and a grant service, so `capacity_unavailable` is a code it can return in principle
 * and never does, and `policy_failed` only fires on a field the server does not model. All three
 * are still handled, and an unrecognised code is reported rather than ignored: the console must
 * never silently proceed past a refusal the server actually issued.
 */
export async function preflightReschedule(
  bookingId: string,
  expectedBookingVersion: number,
  startsAt: string,
  durationMinutes: number,
  partySize: number
): Promise<string | null> {
  let validation;
  try {
    const answer = await validateChange(bookingId, {
      expectedBookingVersion,
      // Exactly the fields this submission will change. The server checks them against its
      // supported-change set, so sending anything it does not model reads as `policy_failed`.
      requestedFields: {
        starts_at: startsAt,
        duration_minutes: durationMinutes,
        party_size: partySize,
      },
    });
    validation = answer?.validation;
  } catch {
    return null;
  }

  if (!validation || validation.valid !== false) return null;

  return refusalMessage(validation.code ?? null);
}

/** The operator-facing wording for each refusal code the server can issue. */
export function refusalMessage(code: string | null): string {
  switch (code) {
    case 'stale_version':
      return 'This booking changed while the console was showing it — the grid has been refreshed. Check the new time before moving it again.';
    case 'capacity_unavailable':
      return 'The venue has no capacity declared for that time, so this move was not submitted.';
    case 'policy_failed':
      return 'The venue’s booking policy does not allow this change.';
    default:
      // A code this console does not model. Say that, rather than inventing a reason or silently
      // proceeding past a refusal the server actually issued.
      return `This change was refused before it was submitted (${code ?? 'no reason given'}).`;
  }
}
