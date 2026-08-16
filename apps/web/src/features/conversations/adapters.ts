/**
 * Adapts session summaries and transcripts to the `Thread` / `Message` shapes
 * `ConversationsScreen` already renders.
 */

import type { Channel, Message, Thread } from "../../data/console";
import type { ConversationSessionSummary, TranscriptView } from "./api";

/**
 * The API's channel vocabulary is `email | messaging | voice | web`. The mapping is explicit
 * and lossy in exactly one direction: `messaging` covers every chat provider and is shown as
 * `sms` rather than guessing a specific one.
 *
 * `email` maps to `email`, not to `web`. It used to collapse into `web`, so an email
 * conversation wore a "Web chat" badge — a small, confident, wrong statement about how the
 * guest reached the venue.
 */
export function toChannel(apiChannel: string | null | undefined): Channel {
  switch (apiChannel) {
    case "voice":
      return "phone";
    case "messaging":
      return "sms";
    case "email":
      return "email";
    case "web":
      return "web";
    default:
      return "web";
  }
}

/** Coarse relative time, matching the mock's `ago` strings. */
export function timeAgo(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "";
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return "";

  const minutes = Math.max(0, Math.floor((now - then) / 60_000));
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hr`;
  return `${Math.floor(hours / 24)} d`;
}

export function toThread(session: ConversationSessionSummary, now = Date.now()): Thread {
  const thread: Thread = {
    id: session.session_id ?? "",
    name: session.customer_ref ?? "Guest",
    channel: toChannel(session.channel),
    preview: session.preview ?? "",
    ago: timeAgo(session.started_at, now),
    // A session with no end time is still open.
    live: !session.ended_at,
    // Carried through verbatim, including `null`. The session read has always returned these
    // two and nothing rendered them, so whether a guest had consented — the record that later
    // proves messaging or recording was permitted — was invisible to the operator.
    consentState: session.consent_state ?? null,
    recordingAllowed: session.recording_allowed ?? null,
  };
  if (session.status) thread.status = session.status as Thread["status"];
  if (session.prov) thread.prov = session.prov;
  return thread;
}

export function toThreads(
  sessions: readonly ConversationSessionSummary[] | null | undefined,
  now = Date.now()
): Thread[] {
  return (sessions ?? []).map((s) => toThread(s, now));
}

/** Maps transcript turns to the screen's message list, ordered by sequence. */
export function toMessages(transcript: TranscriptView | null | undefined): Message[] {
  return [...(transcript?.turns ?? [])]
    .sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0))
    .map((turn) => ({
      from: turn.speaker === "customer" || turn.speaker === "cust" ? "cust" : "agent",
      text: turn.text ?? "",
    }));
}
