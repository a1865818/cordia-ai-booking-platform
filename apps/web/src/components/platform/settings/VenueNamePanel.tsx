import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../../../api/queryKeys";
import { sessionIdentity } from "../../../features/session/identity";
import { getTenantDisplayName, setTenantDisplayName } from "../../../features/settings/tenantProfile";
import { Panel } from "../../ui/Panel";
import { satisfiesRole } from "../../ui/PermissionGate";
import { Icon } from "../../ui/Icon";
import { ErrorState } from "../../ui/ErrorState";
import { PrimaryButton } from "../../ui/Button";
import { Spinner } from "../../ui/Spinner";

/**
 * The venue name a guest is shown (docs §12 gap 11 / §13 B2).
 *
 * This is **not** cosmetic, and the panel says so: it is the only source for spec 08's
 * `business_name`, so until it is set, `GET /api/bookings?phone_e164=` returns no candidates
 * and a guest ringing back cannot be matched to their booking. Deriving one from the tenant id
 * was rejected — it would show a guest a slug — so setting it is a real step, and leaving it
 * unset has a visible consequence rather than a silent one.
 *
 * Owner-only to write (audited server-side); readable by anyone, because the shell needs it.
 */
export function VenueNamePanel() {
  const session = sessionIdentity();
  // One shared port of `StaffRole.Satisfies`, rather than a second hand-written comparison that
  // could drift from the server's matrix.
  const isOwner = satisfiesRole(session.role, 'owner');

  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: queryKeys.settings.venueName(),
    queryFn: ({ signal }) => getTenantDisplayName(signal),
  });

  const [draft, setDraft] = useState('');
  const [touched, setTouched] = useState(false);
  const serverName = query.data?.display_name ?? '';

  // Adopt the server's value until the operator starts typing; after that their edit wins, or a
  // background refetch would wipe it mid-sentence.
  useEffect(() => {
    if (!touched) setDraft(serverName);
  }, [serverName, touched]);

  const save = useMutation({
    mutationFn: (name: string) => setTenantDisplayName(name),
    onSuccess: (saved) => {
      // Seed the cache from the PUT's OWN response body before releasing the draft.
      //
      // `setTouched(false)` hands the input back to the adopt-server effect, and until this line
      // existed that effect ran against a `serverName` the invalidation had not refreshed yet —
      // so a successful save blanked the field back to its pre-save value and left the amber
      // "no venue name is set" banner on screen, asserting something the server had just been
      // told was false. If the refetch then failed (offline, 500) that state persisted, and the
      // operator would reasonably type the name again.
      //
      // The response is the server's own statement, not a local guess, so adopting it is not
      // optimism — the invalidation below still reconciles anything else that changed.
      queryClient.setQueryData(queryKeys.settings.venueName(), saved);
      setTouched(false);
      void queryClient.invalidateQueries({ queryKey: queryKeys.settings.venueName() });
      // The phone lookup's answer changes the moment this lands.
      void queryClient.invalidateQueries({ queryKey: queryKeys.bookings.all() });
    },
  });

  const trimmed = draft.trim();
  const unset = serverName.trim() === '';
  const dirty = trimmed !== serverName.trim();

  return (
    <Panel style={{ marginBottom: 18, padding: '15px' }}>
      <div className="flex items-start gap-[11px] mb-[12px]">
        <span className="w-[34px] h-[34px] rounded-[10px] flex items-center justify-center shrink-0"
          style={{ background: unset ? '#FBF3DF' : '#E7F6EC' }}>
          <Icon name={unset ? 'alert' : 'check'} size={17} strokeWidth={2.1}
            color={unset ? '#C98A09' : '#15924B'} />
        </span>
        <div className="min-w-0">
          <div className="text-[13.5px] font-semibold text-ink">Venue name</div>
          <div className="text-[12px] text-t2 mt-[3px] leading-[1.5]">
            What guests see when they look up their booking. This is the only place the platform
            gets it from.
          </div>
        </div>
      </div>

      {query.isPending && (
        <div className="flex items-center gap-[8px] text-[12.5px] text-t2 py-[6px]">
          <Spinner size={13} /> Loading…
        </div>
      )}

      {query.isError && (
        <ErrorState inline error={query.error} title="Could not read the venue name"
          onRetry={() => { void query.refetch(); }} />
      )}

      {query.isSuccess && (
        <>
          {unset && (
            <div
              role="status"
              className="mb-[11px] rounded-[11px] p-[10px_12px] text-[12px] leading-[1.5]"
              style={{ background: '#FBF3DF', border: '1px solid #F0DCA8', color: '#7E5606' }}
            >
              No venue name is set, so <strong>looking a booking up by phone returns nothing</strong>.
              A guest ringing back cannot be matched to their booking until this is filled in.
            </div>
          )}

          <label className="block">
            <div className="text-[12px] font-semibold mb-[5px]" style={{ color: '#55554F' }}>
              Name
            </div>
            <input
              value={draft}
              onChange={e => { setDraft(e.target.value); setTouched(true); }}
              placeholder="Serenity Spa"
              maxLength={200}
              disabled={!isOwner || save.isPending}
              className="w-full h-[38px] border border-cd-border rounded-[9px] px-3 text-[13px] text-ink outline-none focus:border-[#2F6BEB] transition-colors disabled:opacity-60"
            />
          </label>

          {!isOwner ? (
            <div className="text-[11.5px] text-t3 mt-[7px] leading-[1.5]">
              Only an owner can change this. Your session is <strong>{session.role || 'unset'}</strong>,
              so the platform would refuse the write — the field is shown, not offered.
            </div>
          ) : (
            <div className="mt-[11px]">
              <PrimaryButton
                disabled={trimmed.length === 0 || !dirty || save.isPending}
                busy={save.isPending}
                onClick={() => save.mutate(trimmed)}
              >
                {dirty ? 'Save venue name' : 'Saved'}
              </PrimaryButton>
            </div>
          )}

          {save.isError && (
            <div className="mt-[10px]">
              <ErrorState inline error={save.error} title="Could not save the venue name"
                onRetry={() => save.mutate(trimmed)} />
            </div>
          )}
        </>
      )}
    </Panel>
  );
}
