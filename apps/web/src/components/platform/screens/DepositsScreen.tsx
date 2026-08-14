import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { money } from "../../../data/console";
import { Avatar } from "../../ui/Avatar";
import { SectionLabel } from "../../ui/SectionLabel";
import { DepChip } from "../../ui/StatusTags";
import { Pills } from "../../ui/Pills";
import { ErrorState } from "../../ui/ErrorState";
import { Skeleton } from "../../ui/Skeleton";
import { ReasonCodeDialog, composeReason } from "../../ui/ReasonCodeDialog";
import { queryKeys } from "../../../api/queryKeys";
import { useDeposits, usePayoutSummary } from "../../../features/hooks";
import { refundDepositWithReason, toConsoleDeposit } from "../../../features/deposits/api";

export function DepositsScreen() {
  const queryClient = useQueryClient();
  const [depFilter, setDepFilter] = useState("all");
  const depositsQuery = useDeposits(depFilter === "all" ? undefined : depFilter);
  const payoutQuery = usePayoutSummary();
  const [refundId, setRefundId] = useState<string | null>(null);
  const [refundBusy, setRefundBusy] = useState(false);

  const payout = payoutQuery.data;
  const rows = (depositsQuery.data?.deposits ?? []).map(toConsoleDeposit);
  const notConnected = !payout || payout.account === "Not connected";

  async function confirmRefund(reasonCode: string, note: string) {
    if (!refundId) return;
    setRefundBusy(true);
    try {
      await refundDepositWithReason(refundId, composeReason(reasonCode, note) || "deposit_refund");
      void queryClient.invalidateQueries({ queryKey: queryKeys.deposits.all() });
      void queryClient.invalidateQueries({ queryKey: queryKeys.payouts.all() });
      setRefundId(null);
    } finally {
      setRefundBusy(false);
    }
  }

  return (
    <div>
      {payoutQuery.isError && (
        <div className="mb-3">
          <ErrorState inline error={payoutQuery.error} onRetry={() => void payoutQuery.refetch()} />
        </div>
      )}

      <div className="bg-ink rounded-[16px] p-[18px_19px] mb-4 text-white">
        <div className="flex items-start justify-between">
          <div>
            {payout?.next_date ? (
              <>
                <div className="font-mono text-[10px] tracking-[0.14em] uppercase" style={{ color: "rgba(255,255,255,0.55)" }}>
                  Next payout · {payout.next_date}
                </div>
                {payout.next_amount_minor != null && (
                  <span className="font-mono text-[34px] font-bold tracking-[-0.03em] text-white block mt-[6px]">
                    {money((payout.next_amount_minor / 100).toLocaleString())}
                  </span>
                )}
              </>
            ) : (
              <div className="font-mono text-[10px] tracking-[0.14em] uppercase" style={{ color: "rgba(255,255,255,0.55)" }}>
                Payouts
              </div>
            )}
          </div>
          <span
            className="inline-flex items-center gap-[5px] text-[11px] font-semibold px-[10px] py-1 rounded-full"
            style={{ background: "rgba(255,255,255,0.12)", color: "rgba(255,255,255,0.85)" }}
          >
            {notConnected ? "Not connected" : (payout?.account ?? "")}
          </span>
        </div>
        <div className="flex gap-6 mt-4 pt-[15px] border-t" style={{ borderColor: "rgba(255,255,255,0.12)" }}>
          <div>
            <span className="font-mono text-[16px] font-bold text-white">
              {payout ? money((payout.held_minor / 100).toLocaleString()) : "—"}
            </span>
            <div className="text-[11px] mt-[2px]" style={{ color: "rgba(255,255,255,0.55)" }}>held on bookings</div>
          </div>
          <div>
            <span className="font-mono text-[16px] font-bold text-white">
              {payout ? money((payout.available_minor / 100).toLocaleString()) : "—"}
            </span>
            <div className="text-[11px] mt-[2px]" style={{ color: "rgba(255,255,255,0.55)" }}>available</div>
          </div>
          {payout && payout.account !== "Not connected" && (
            <div className="ml-auto self-end">
              <span className="font-mono text-[11px]" style={{ color: "rgba(255,255,255,0.5)" }}>{payout.account}</span>
            </div>
          )}
        </div>
      </div>

      <SectionLabel line>Recent deposits</SectionLabel>

      <div className="mb-3">
        <Pills
          value={depFilter}
          onChange={setDepFilter}
          options={[
            { value: "all", label: "All" },
            { value: "captured", label: "Captured" },
            { value: "held", label: "Held" },
            { value: "failed", label: "Failed" },
            { value: "refunded", label: "Refunded" },
          ]}
        />
      </div>

      {depositsQuery.isPending && (
        <div className="flex flex-col gap-2">
          <Skeleton h={64} /><Skeleton h={64} /><Skeleton h={64} />
        </div>
      )}

      {!depositsQuery.isPending && depositsQuery.isError && (
        <ErrorState inline error={depositsQuery.error} onRetry={() => void depositsQuery.refetch()} />
      )}

      {!depositsQuery.isPending && !depositsQuery.isError && (
        <div className="flex flex-col gap-2">
          {rows.map((d) => (
            <div key={d.id} className="flex items-center gap-3 bg-white border border-cd-border rounded-[13px] p-[12px_14px]">
              <Avatar name={d.name} size={36} radius={10} font="13px" />
              <div className="flex-1 min-w-0">
                <div className="text-[14px] font-semibold text-ink truncate">{d.name}</div>
                <div className="text-[11.5px] text-t2 mt-[1px]">
                  {[d.service, d.when].filter(Boolean).join(" · ")}
                </div>
              </div>
              <div className="flex flex-col items-end gap-[5px] shrink-0">
                <span
                  className="font-mono text-[14px] font-bold"
                  style={{
                    color: d.status === "refunded" ? "#9A9A9A" : "#0A0A0A",
                    textDecoration: d.status === "refunded" ? "line-through" : "none",
                  }}
                >
                  {money(d.amount)}
                </span>
                <DepChip status={d.status} />
                {d.status === "captured" && (
                  <button
                    type="button"
                    onClick={() => setRefundId(d.id)}
                    className="text-[11px] font-semibold text-[#2F6BEB] bg-transparent border-none cursor-pointer p-0 hover:underline"
                    style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
                  >
                    Refund
                  </button>
                )}
              </div>
            </div>
          ))}
          {rows.length === 0 && (
            <div className="text-center text-[13px] text-t3 py-8">
              {depFilter === "all" ? "No deposits" : `No ${depFilter} deposits`}
            </div>
          )}
        </div>
      )}
      <div className="h-2" />

      <ReasonCodeDialog
        open={!!refundId}
        busy={refundBusy}
        title="Refund this deposit"
        body="Records a local refund. There is no Stripe payout — this marks the captured session as refunded."
        confirmLabel="Refund"
        onCancel={() => setRefundId(null)}
        onConfirm={confirmRefund}
      />
    </div>
  );
}
