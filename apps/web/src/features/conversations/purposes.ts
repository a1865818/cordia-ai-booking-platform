/**
 * Why an operator is opening a transcript (spec 14 §2 "role/purpose-restricted transcript
 * access", §3.1 step 4).
 *
 * The value travels as `X-Reason-Code` and is written verbatim into `audit_events`. It used to be
 * the constant `"operator_review"` baked into the fetcher, which satisfied "a reason was recorded"
 * and defeated the point: an audit trail where every row reads the same cannot distinguish a
 * dispute investigation from someone idly browsing.
 *
 * This list mirrors `SensitiveReadPurpose` in `apps/api` — the server rejects anything outside it,
 * so adding an option here without adding it there produces a 400.
 */

export interface SensitiveReadPurposeOption {
  value: SensitiveReadPurpose;
  /** Shown to the operator. Phrased as the reason itself, not as a category name. */
  label: string;
}

export const SENSITIVE_READ_PURPOSES = [
  { value: "quality_review", label: "Routine quality check" },
  { value: "guest_complaint", label: "Following up a complaint" },
  { value: "dispute_review", label: "Investigating a dispute" },
  { value: "guest_request", label: "The guest asked about this" },
  { value: "training_review", label: "Coaching or agent tuning" },
  { value: "security_investigation", label: "Security or fraud investigation" },
  { value: "legal_hold", label: "Legal hold or lawful request" },
] as const satisfies readonly { value: string; label: string }[];

export type SensitiveReadPurpose = (typeof SENSITIVE_READ_PURPOSES)[number]["value"];
