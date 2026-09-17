import { useEffect, useState } from "react";
import { PrimaryButton, GhostButton } from "../../components/ui/Button";
import { Icon } from "../../components/ui/Icon";
import { ErrorState } from "../../components/ui/ErrorState";
import { useVerification } from "./useVerification";
import type { VerificationAction } from "./api";

interface VerificationGateProps {
  action: VerificationAction;
  targetBookingId?: string;
  customerId?: string;
  /** What the operator is trying to do, e.g. "see this booking's details". */
  purpose: string;
  /** Called with the grant id once the server issues one. */
  onGranted: (grantId: string) => void;
  onCancel?: () => void;
}

/**
 * Step-up verification for a sensitive read or mutation (spec 08, Inv-11).
 *
 * Mounted when the API answers `requires_verification`: the guest receives a code, reads
 * it back, and the server — not this component — decides whether to issue a grant. The
 * grant never leaves memory.
 */
export function VerificationGate({
  action,
  targetBookingId,
  customerId,
  purpose,
  onGranted,
  onCancel,
}: VerificationGateProps) {
  const verification = useVerification({ action, targetBookingId, customerId });
  const [code, setCode] = useState("");

  const grantId = verification.grant?.grantId;

  // Hand the grant up from an effect, not from render: calling a parent's setState during
  // render is a side effect React is entitled to run twice.
  useEffect(() => {
    if (verification.phase === "granted" && grantId) onGranted(grantId);
  }, [verification.phase, grantId, onGranted]);

  if (verification.phase === "granted") return null;

  const busy = verification.phase === "sending" || verification.phase === "verifying";

  return (
    <div
      className="rounded-[13px] p-[16px]"
      style={{ background: '#FBFBFA', border: '1px solid #EFEFEF' }}
      role="group"
      aria-label="Verify the guest before continuing"
    >
      <div className="flex items-start gap-[11px]">
        <span
          className="w-[34px] h-[34px] rounded-[10px] flex items-center justify-center shrink-0"
          style={{ background: '#FBF3DF' }}
        >
          <Icon name="shield" size={17} strokeWidth={2} color="#8A6400" />
        </span>
        <div className="flex-1 min-w-0">
          <div className="text-[14px] font-semibold text-ink">Verify the guest first</div>
          <div className="text-[12.5px] text-t2 mt-[3px] leading-[1.5]">
            To {purpose}, send the guest a code and enter what they read back.
          </div>
        </div>
      </div>

      {verification.developmentCode && (
        // Only ever present where the API is configured to return it — never in a deployment.
        <div className="text-[12px] mt-[10px] font-mono" style={{ color: '#55554F' }} role="note">
          Development code: {verification.developmentCode}
        </div>
      )}

      {verification.message && (
        <div className="text-[12px] mt-[10px]" style={{ color: '#8A6400' }} role="status">
          {verification.message}
        </div>
      )}

      {verification.error != null && (
        <div className="mt-[10px]">
          <ErrorState inline error={verification.error} />
        </div>
      )}

      {verification.phase === "idle" || verification.phase === "sending" || verification.phase === "failed" ? (
        <div className="flex gap-2 mt-[13px]">
          <PrimaryButton busy={verification.phase === "sending"} onClick={() => { void verification.start("sms"); }}>
            Send code by SMS
          </PrimaryButton>
          <GhostButton
            onClick={() => { if (!busy) void verification.start("email"); }}
            style={busy ? { opacity: 0.5, cursor: 'not-allowed' } : undefined}
          >
            Email instead
          </GhostButton>
          {onCancel && <GhostButton onClick={onCancel}>Cancel</GhostButton>}
        </div>
      ) : (
        <div className="mt-[13px]">
          <label className="block">
            <span className="text-[12px] font-semibold" style={{ color: '#55554F' }}>
              Code from the guest
            </span>
            <input
              value={code}
              onChange={(e) => setCode(e.target.value)}
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={10}
              aria-label="Verification code"
              className="w-full h-[42px] mt-[6px] border border-cd-border rounded-[10px] px-3 text-[15px] tracking-[0.18em] text-ink outline-none focus:border-[#2F6BEB] transition-colors"
              style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
            />
          </label>
          <div className="flex gap-2 mt-[11px]">
            <PrimaryButton
              busy={verification.phase === "verifying"}
              disabled={code.trim().length === 0 || busy}
              onClick={() => { void verification.submitCode(code.trim()); }}
            >
              Verify
            </PrimaryButton>
            <GhostButton onClick={() => { verification.reset(); setCode(""); }}>Start over</GhostButton>
            {onCancel && <GhostButton onClick={onCancel}>Cancel</GhostButton>}
          </div>
        </div>
      )}
    </div>
  );
}
