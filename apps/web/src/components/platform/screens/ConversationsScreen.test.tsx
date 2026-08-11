import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { ConversationsScreen } from "./ConversationsScreen";
import { ErrorState } from "../../ui/ErrorState";
import type { Message, Thread } from "../../../data/console";
import { toThread } from "../../../features/conversations/adapters";

const liveThread: Thread = {
  id: "sess_live",
  name: "cust_9f2",
  channel: "web",
  preview: "",
  ago: "3 min",
  live: false,
};

function renderScreen(props: Partial<Parameters<typeof ConversationsScreen>[0]> = {}) {
  const messages: Record<string, Message[]> = { sess_live: [] };
  return render(
    <ConversationsScreen
      device="desktop"
      threads={[liveThread]}
      messages={messages}
      convOpen="sess_live"
      convView="thread"
      openConv={() => {}}
      backConv={() => {}}
      takeOver={() => {}}
      {...props}
    />
  );
}

describe("ConversationsScreen transcript failures", () => {
  it("names a failed transcript read instead of rendering an empty conversation", () => {
    // The pane used to fall back to the mock MESSAGES map, which has no entry for a real
    // session id — so a 403 or an outage displayed a clean, empty, confident thread.
    renderScreen({
      transcriptError: (
        <ErrorState inline error={new Error("boom")} title="Couldn’t load this transcript." />
      ),
    });

    expect(screen.getByText("Couldn’t load this transcript.")).toBeInTheDocument();
  });

  it("says the transcript is loading rather than showing it as empty", () => {
    renderScreen({ transcriptPending: true });
    expect(screen.getByText("Loading the transcript…")).toBeInTheDocument();
  });

  it("renders the messages when the read succeeded", () => {
    renderScreen({
      messages: { sess_live: [{ from: "cust", text: "can I move my booking" }] },
    });

    expect(screen.getByText("can I move my booking")).toBeInTheDocument();
    expect(screen.queryByText("Loading the transcript…")).not.toBeInTheDocument();
  });

  it("claims no provenance for a live thread", () => {
    // Nothing server-side records who handled a session, so the "handled by AI" line and the
    // AI badge must not appear on a row built from the API.
    renderScreen();

    expect(screen.queryByText(/handled by AI/i)).not.toBeInTheDocument();
    expect(screen.queryByText("AI")).not.toBeInTheDocument();
  });

  it("still shows provenance for a demo thread that records it", () => {
    renderScreen({ threads: [{ ...liveThread, prov: "ai" }] });
    expect(screen.getByText(/handled by AI/i)).toBeInTheDocument();
  });
});

describe("toThread", () => {
  it("asserts neither an outcome nor provenance", () => {
    const thread = toThread(
      {
        session_id: "sess_1",
        channel: "web",
        customer_ref: "cust_1",
        consent_state: null,
        recording_allowed: null,
        started_at: "2026-08-03T09:00:00Z",
        ended_at: "2026-08-03T09:10:00Z",
      } as Parameters<typeof toThread>[0],
      new Date("2026-08-03T09:15:00Z").getTime()
    );

    // Was `status: "pending"` / `prov: "ai"` for every session, ended or not.
    expect(thread.status).toBeUndefined();
    expect(thread.prov).toBeUndefined();
    expect(thread.live).toBe(false);
  });
});
