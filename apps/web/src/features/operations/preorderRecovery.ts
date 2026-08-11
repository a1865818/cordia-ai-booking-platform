/**
 * Pre-order manual resolution (spec 11 §21.4).
 *
 * When a pre-order saga parks, an operator has exactly two exits, and they are not symmetric:
 *
 *  • **Convert to standalone** — the booking is gone but the customer still wants the order.
 *    §21.4 is explicit that a pre-order never survives a failed or cancelled booking *unless
 *    the operator AND the customer both agree*, so the server demands a spec-08 verification
 *    grant evidencing the customer's agreement. `grantId` is therefore not optional plumbing:
 *    it is the customer's half of the decision, and the console must obtain a real grant
 *    rather than inventing one.
 *
 *  • **Cancel** — the "or cancel" path. No grant: nothing is being kept on the customer's
 *    behalf, so there is nothing for them to agree to.
 *
 * Both are operator actions with the fail-closed spine (`X-Actor`, `X-Actor-Role`,
 * `X-Reason-Code`), all rejected server-side when empty, so none is defaulted here. Neither
 * carries an idempotency key, so neither may be auto-retried; both can legitimately 409 when
 * the saga has already moved.
 */

import { fetchApi } from "../../api/client";

export interface ConvertPreorderToStandaloneInput {
  sagaId: string;
  /**
   * The customer's single-use spec-08 verification grant evidencing agreement to the
   * conversion. Required — see §21.4 above.
   */
  grantId: string;
  /** The operator's own justification. Audited; never defaulted. */
  reasonCode: string;
}

export interface CancelPreorderInput {
  sagaId: string;
  reasonCode: string;
  /** Free-text operator note recorded with the cancellation. */
  reason?: string;
}

/** Echoed `{ saga_id, status }`. The status is the server's word for what happened. */
export interface PreorderResolutionResult {
  saga_id?: string;
  status?: string;
}

export function convertPreorderToStandalone(input: ConvertPreorderToStandaloneInput) {
  return fetchApi<PreorderResolutionResult>(
    `/api/preorder/sagas/${encodeURIComponent(input.sagaId)}/convert-standalone`,
    {
      method: "POST",
      actor: true,
      reasonCode: input.reasonCode,
      body: JSON.stringify({ grant_id: input.grantId }),
    }
  );
}

export function cancelPreorder(input: CancelPreorderInput) {
  return fetchApi<PreorderResolutionResult>(
    `/api/preorder/sagas/${encodeURIComponent(input.sagaId)}/cancel`,
    {
      method: "POST",
      actor: true,
      reasonCode: input.reasonCode,
      body: JSON.stringify({ reason: input.reason ?? null }),
    }
  );
}
