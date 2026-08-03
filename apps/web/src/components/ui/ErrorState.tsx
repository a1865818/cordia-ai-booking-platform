import { isApiError } from "../../api/problem";
import { ApiValidationError } from "../../api/problem";
import { Icon } from "./Icon";
import { GhostButton } from "./Button";

interface ErrorStateProps {
  error: unknown;
  /** Rendered as the heading. Defaults to a message derived from the error. */
  title?: string;
  onRetry?: () => void;
  /** Compact variant for inline panels rather than a full screen. */
  inline?: boolean;
}

/**
 * Renders an API failure the way the server described it (N9: help users diagnose and
 * recover). The API's `ProblemDetails.detail`/`title` is the most specific explanation
 * available, so it is shown verbatim rather than replaced with a generic string; a
 * `ValidationProblemDetails` additionally lists its per-field messages.
 */
export function ErrorState({ error, title, onRetry, inline }: ErrorStateProps) {
  const status = isApiError(error) ? error.status : undefined;
  const detail =
    error instanceof Error && error.message ? error.message : "Something went wrong.";

  const heading = title ?? headingFor(status);
  const fields = error instanceof ApiValidationError ? error.fieldMessages() : [];

  return (
    <div
      role="alert"
      className={
        inline
          ? "flex items-start gap-[10px] rounded-[11px] p-[12px_13px]"
          : "flex flex-col items-center justify-center text-center py-[52px] px-[24px]"
      }
      style={inline ? { background: '#FDF2F2', border: '1px solid #F5DADA' } : undefined}
    >
      {inline ? (
        <Icon name="alert" size={16} strokeWidth={2.1} color="#C0392B" />
      ) : (
        <span
          className="w-[60px] h-[60px] rounded-full flex items-center justify-center mb-[18px]"
          style={{ background: '#FDF2F2' }}
        >
          <Icon name="alert" size={28} strokeWidth={2.2} color="#C0392B" />
        </span>
      )}

      <div className={inline ? "flex-1 min-w-0" : ""}>
        <div
          className={
            inline
              ? "text-[12.5px] font-semibold text-ink"
              : "text-[19px] font-bold tracking-[-0.02em] text-ink"
          }
        >
          {heading}
        </div>
        <div
          className={
            inline
              ? "text-[11.5px] text-t2 mt-[3px] leading-[1.45]"
              : "text-[13.5px] text-t2 mt-[6px] max-w-[360px] leading-[1.5]"
          }
        >
          {detail}
        </div>

        {fields.length > 0 && (
          <ul className={inline ? "mt-[5px]" : "mt-[8px] text-left inline-block"}>
            {fields.map((message) => (
              <li key={message} className="text-[11.5px] text-t3 leading-[1.5]">
                {message}
              </li>
            ))}
          </ul>
        )}

        {onRetry && (
          <div className={inline ? "mt-[7px]" : "mt-[16px]"}>
            <GhostButton onClick={onRetry} icon="clock">
              Try again
            </GhostButton>
          </div>
        )}
      </div>
    </div>
  );
}

function headingFor(status: number | undefined): string {
  switch (status) {
    case undefined:
      return "Couldn't reach the server";
    case 400:
      return "That request wasn't valid";
    case 401:
      return "Your session needs to be re-established";
    case 403:
      return "Verification needed";
    case 404:
      return "Not found";
    case 409:
      return "This changed while you were looking";
    case 422:
      return "The server couldn't accept that";
    default:
      return status >= 500 ? "The server had a problem" : "Something went wrong";
  }
}
