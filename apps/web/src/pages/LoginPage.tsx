import { FormEvent, useId, useState } from "react";
import { Link, useNavigate } from "react-router";
import { useQueryClient } from "@tanstack/react-query";
import { beginSession } from "../features/auth/sessionTransition";
import { login } from "../features/auth/api";
import { PrimaryButton } from "../components/ui/Button";
import { ErrorState } from "../components/ui/ErrorState";

const inputClass =
  "w-full h-[42px] border border-cd-border rounded-[10px] px-3 text-[14px] text-ink outline-none focus:border-[#2F6BEB] transition-colors";

const labelClass = "font-mono text-[10px] tracking-[0.14em] uppercase text-t3 block mb-[7px]";

export function LoginPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const emailId = useId();
  const passwordId = useId();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const session = await login(email.trim(), password);
      beginSession(queryClient, session);
      navigate("/dashboard");
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div
      className="min-h-screen flex flex-col items-center justify-center px-5 py-10"
      style={{
        background: "#F6F6F4",
        fontFamily: "'Inter Tight', system-ui, sans-serif",
        WebkitFontSmoothing: "antialiased",
      }}
    >
      <div className="w-full max-w-[400px]">
        <Link to="/" className="inline-flex items-center gap-2 font-sans font-bold text-[23px] tracking-[-0.045em] text-ink no-underline">
          <span className="w-7 h-7 rounded-[7px] bg-ink grid place-items-center shrink-0">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" aria-hidden="true">
              <path d="M5 6.5C5 5.4 5.9 4.5 7 4.5h10c1.1 0 2 .9 2 2v7c0 1.1-.9 2-2 2H10l-4 3.2c-.5.4-1.2 0-1.2-.6V6.5Z" fill="#fff" />
              <circle cx="9" cy="10" r="1.2" fill="#0A0A0A" />
              <circle cx="12.5" cy="10" r="1.2" fill="#0A0A0A" />
              <circle cx="16" cy="10" r="1.2" fill="#0A0A0A" />
            </svg>
          </span>
          Cordia
        </Link>

        <div className="mt-8 bg-white border border-cd-border rounded-[14px] p-[22px_22px_20px] shadow-[0_12px_32px_-16px_rgba(10,10,10,0.12)]">
          <div className="font-mono text-[10px] tracking-[0.14em] uppercase text-t3 mb-[6px]">Console</div>
          <h1 className="text-[22px] font-bold tracking-[-0.03em] text-ink m-0 leading-[1.15]">
            Sign in
          </h1>
          <p className="text-[13.5px] text-t2 mt-[8px] mb-5 leading-[1.45]">
            Use your operator account to open the venue console.
          </p>

          {error != null && (
            <div className="mb-4">
              <ErrorState inline error={error} title="Couldn't sign in" />
            </div>
          )}

          <form onSubmit={onSubmit} className="flex flex-col gap-[14px]">
            <div>
              <label htmlFor={emailId} className={labelClass}>Email</label>
              <input
                id={emailId}
                type="email"
                name="email"
                autoComplete="username"
                required
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className={inputClass}
                style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
              />
            </div>
            <div>
              <label htmlFor={passwordId} className={labelClass}>Password</label>
              <input
                id={passwordId}
                type="password"
                name="password"
                autoComplete="current-password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className={inputClass}
                style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
              />
            </div>
            <PrimaryButton type="submit" full busy={busy} disabled={busy}>
              Sign in
            </PrimaryButton>
          </form>

          <p className="mt-[14px] font-mono text-[11px] tracking-[0.02em] text-t3 leading-[1.5] m-0">
            Development · <span className="text-t2">owner@demo.local</span>
            {" / "}
            <span className="text-t2">localdev</span>
          </p>
        </div>

        <p className="mt-4 text-center">
          <Link to="/" className="text-[13px] text-t2 no-underline hover:text-ink transition-colors">
            ← Back to Cordia
          </Link>
        </p>
      </div>
    </div>
  );
}
