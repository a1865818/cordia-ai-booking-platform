import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../../../api/queryKeys";
import { SESSION_IDENTITY_HINT, sessionIdentity } from "../../../features/session/identity";
import { declareSlot } from "../../../features/bookings/availability";
import { Panel } from "../../ui/Panel";
import { PermissionGate } from "../../ui/PermissionGate";
import { SectionLabel } from "../../ui/SectionLabel";
import { ErrorState } from "../../ui/ErrorState";
import { PrimaryButton } from "../../ui/Button";

const inputClass =
  "w-full h-[40px] border border-cd-border rounded-[10px] px-3 text-[13.5px] text-ink outline-none focus:border-[#2F6BEB] transition-colors";

/**
 * Declares what the venue can take (`POST /api/availability/slots`).
 *
 * **This ships as the other half of the availability read, and that pairing is the point.**
 * `capacity_slots` had a reader and no writer for its whole life, so the table was empty in
 * every deployment: no booking was ever capacity-checked, and an availability read on its own
 * would have reported "nothing available" for every venue on earth. A read over a table nothing
 * fills is not a feature — so the console does not get one without the surface that fills it.
 *
 * Owner-only and audited server-side, because declaring capacity is a commercial statement
 * about what the venue will honour, not a display preference. The reason code is never
 * defaulted: an audit row that always says the same thing records nothing.
 */
export function CapacitySlotPanel() {
  const session = sessionIdentity();
  const queryClient = useQueryClient();

  const [serviceId, setServiceId] = useState('');
  const [startsAt, setStartsAt] = useState('');
  const [capacity, setCapacity] = useState('1');
  const [reason, setReason] = useState('');

  const declare = useMutation({
    mutationFn: () => declareSlot(
      {
        serviceId: serviceId.trim(),
        startsAt: new Date(startsAt).toISOString(),
        totalCapacity: Number(capacity),
      },
      reason.trim()
    ),
    onSuccess: () => {
      // The create drawer's advisory note reads this; a slot declared here must show up there
      // without a reload, or the operator cannot tell whether the declaration took.
      void queryClient.invalidateQueries({ queryKey: queryKeys.bookings.all() });
    },
  });

  const startsAtValid = startsAt !== '' && !Number.isNaN(Date.parse(startsAt));
  const capacityNumber = Number(capacity);
  // A slot belonging to nothing cannot be matched to a booking, so it would be capacity the
  // platform can never spend.
  const canSubmit =
    serviceId.trim() !== '' && startsAtValid &&
    Number.isInteger(capacityNumber) && capacityNumber > 0 &&
    reason.trim() !== '' && !declare.isPending;

  return (
    <Panel style={{ marginBottom: 18 }}>
      <SectionLabel>Bookable capacity</SectionLabel>
      <div title={SESSION_IDENTITY_HINT}>
        <PermissionGate
          requiredRole="owner"
          role={session.role}
          capability="Declaring bookable capacity"
        >
          <>
            <div className="text-[12.5px] text-t2 leading-[1.5] mb-[13px]">
              A declared slot is what the platform checks a booking against. Until a venue
              declares some, nothing is capacity-checked and the create drawer says so rather
              than implying every time is free. Re-declaring the same slot is safe — it returns
              the existing one instead of resetting counts live bookings depend on.
            </div>

            <label className="block mb-[6px]">
              <div className="text-[11.5px] text-t3 mb-[4px]">Service id</div>
              <input value={serviceId} onChange={e => setServiceId(e.target.value)}
                placeholder="cat_1:gel" aria-label="Service id" className={inputClass} />
            </label>
            {/* The endpoint also accepts resource_id and location_id, and this panel deliberately
                does NOT offer them. A slot's key is `{dimension}|{starts_at}` where the dimension
                is the FIRST non-empty of resource → service → location, and a booking derives its
                key the same way from its canonical payload — which carries `service_id` and never
                a resource. So a slot declared against a resource is keyed on something no booking
                can produce: it would sit in the table, be listed as available, and never be
                matched or enforced. Offering a field whose only effect is to create unenforceable
                capacity is worse than not offering it. */}
            <div className="text-[11px] text-t3 mb-[13px] leading-[1.5]">
              A booking is matched to a slot by <strong>service and start time</strong>, so that is
              what a slot is declared against. (The API also accepts resource and location
              dimensions; nothing that books today emits them, so a slot keyed on one could never
              be matched — it is not offered here for that reason.)
            </div>

            <div className="flex gap-[10px] mb-[13px]">
              <label className="flex-1">
                <div className="text-[11.5px] text-t3 mb-[4px]">Starts at</div>
                <input type="datetime-local" value={startsAt}
                  onChange={e => setStartsAt(e.target.value)}
                  aria-label="Slot start" className={inputClass} />
              </label>
              <label className="flex-1">
                <div className="text-[11.5px] text-t3 mb-[4px]">Total capacity</div>
                <input type="number" min={1} value={capacity}
                  onChange={e => setCapacity(e.target.value)}
                  aria-label="Total capacity" className={inputClass} />
              </label>
            </div>

            <label className="block mb-[13px]">
              <div className="text-[11.5px] text-t3 mb-[4px]">Reason (recorded on the audit trail)</div>
              <input value={reason} onChange={e => setReason(e.target.value)}
                placeholder="Why this capacity is being declared"
                aria-label="Reason" className={inputClass} />
            </label>

            {declare.isError && (
              <div className="mb-[13px]">
                <ErrorState inline error={declare.error} title="Couldn’t declare this slot." />
              </div>
            )}
            {declare.isSuccess && (
              <div className="text-[12px] mb-[13px]" style={{ color: '#14663A' }} role="status">
                Slot declared. It now appears in the create drawer’s availability note.
              </div>
            )}

            <PrimaryButton
              busy={declare.isPending}
              disabled={!canSubmit}
              onClick={() => declare.mutate()}
            >
              {declare.isPending ? 'Declaring' : 'Declare slot'}
            </PrimaryButton>
          </>
        </PermissionGate>
      </div>
    </Panel>
  );
}
