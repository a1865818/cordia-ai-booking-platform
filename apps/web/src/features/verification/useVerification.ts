/**
 * Drives the step-up verification flow and holds the resulting grant.
 *
 * The grant is kept **in memory only** — never `localStorage` or `sessionStorage`. It is a
 * short-lived authorization to read or change someone else's booking; persisting it would
 * outlive both the operator's session and the server's expiry.
 */

import { useCallback, useState } from "react";
import { requestVerification, verifyCode } from "./api";
import type { VerificationAction, VerificationChannel } from "./api";

export type VerificationPhase = "idle" | "sending" | "awaiting_code" | "verifying" | "granted" | "failed";

export interface VerificationGrant {
  grantId: string;
  expiresAt?: string;
  allowedAction?: string;
}

export interface VerificationState {
  phase: VerificationPhase;
  grant: VerificationGrant | null;
  error: unknown;
  /** Why the last attempt did not grant, in operator language. */
  message: string | null;
}

const INITIAL: VerificationState = { phase: "idle", grant: null, error: null, message: null };

function randomKey(): string {
  return typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
    ? `vrf_${crypto.randomUUID().split("-").join("")}`
    : `vrf_${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
}

export function useVerification(options: {
  action: VerificationAction;
  targetBookingId?: string;
  customerId?: string;
}) {
  const [state, setState] = useState<VerificationState>(INITIAL);
  const [verificationId, setVerificationId] = useState<string | null>(null);
  // A key per *send*, not per component. The server returns the original challenge
  // verbatim for a repeated key without checking the channel, so reusing one key would
  // make "email instead" silently re-report the SMS challenge.
  const [idempotencyKey, setIdempotencyKey] = useState(randomKey);

  const start = useCallback(
    async (channel: VerificationChannel) => {
      if (state.phase === "sending" || state.phase === "verifying") return;

      setState({ ...INITIAL, phase: "sending" });
      try {
        const response = await requestVerification({
          action: options.action,
          channel,
          customerId: options.customerId,
          targetBookingId: options.targetBookingId,
          idempotencyKey,
        });

        const status = response.status;
        if (status === "no_verified_destination") {
          setState({
            phase: "failed",
            grant: null,
            error: null,
            message: "No verified phone or email is on file for this booking.",
          });
          return;
        }
        if (status === "rate_limited") {
          setState({
            phase: "failed",
            grant: null,
            error: null,
            message: "Too many codes requested. Wait a moment before trying again.",
          });
          return;
        }

        setVerificationId(response.verification?.verification_id ?? null);
        setState({
          phase: "awaiting_code",
          grant: null,
          error: null,
          // `already_sent` means the server replayed an earlier challenge rather than
          // issuing a new one — say so, instead of implying a fresh code just went out.
          message:
            status === "already_sent"
              ? "A code was already sent — ask the guest for the one they have."
              : null,
        });
      } catch (error) {
        setState({ phase: "failed", grant: null, error, message: null });
      }
    },
    [idempotencyKey, state.phase, options.action, options.customerId, options.targetBookingId]
  );

  const submitCode = useCallback(
    async (code: string) => {
      if (!verificationId) return;
      setState((s) => ({ ...s, phase: "verifying", error: null, message: null }));

      try {
        const response = await verifyCode(verificationId, code);

        if (response.status === "granted" && response.grant?.grant_id) {
          setState({
            phase: "granted",
            grant: {
              grantId: response.grant.grant_id,
              expiresAt: response.grant.expires_at,
              allowedAction: response.grant.allowed_action,
            },
            error: null,
            message: null,
          });
          return;
        }

        // `failed` / `expired` / `pending` are business outcomes, not transport errors.
        setState({
          phase: response.status === "expired" ? "failed" : "awaiting_code",
          grant: null,
          error: null,
          message:
            response.status === "expired"
              ? "That code expired. Send a new one."
              : "That code did not match. Check it and try again.",
        });
      } catch (error) {
        setState((s) => ({ ...s, phase: "awaiting_code", error, message: null }));
      }
    },
    [verificationId]
  );

  const reset = useCallback(() => {
    setState(INITIAL);
    setVerificationId(null);
    // A fresh key, or the next send would replay the challenge just abandoned.
    setIdempotencyKey(randomKey());
  }, []);

  return { ...state, start, submitCode, reset };
}
