import { FormEvent, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../../../api/queryKeys";
import { useStaffResources, useTenantProfile } from "../../../features/hooks";
import { createStaffResource } from "../../../features/resources/api";
import { BUSINESS_TYPES } from "../../../data/console";
import { Panel } from "../../ui/Panel";
import { SectionLabel } from "../../ui/SectionLabel";
import { ErrorState } from "../../ui/ErrorState";
import { Skeleton } from "../../ui/Skeleton";

/**
 * The people/tables/rooms the schedule grid draws as lanes (`GET`/`POST /api/resources`).
 *
 * Distinct from `/api/tenant-resources` (spoken names for services and locations). An empty
 * roster is a real answer: the grid then draws one unnamed lane rather than inventing staff.
 */
export function StaffRosterPanel() {
  const queryClient = useQueryClient();
  const roster = useStaffResources();
  const profile = useTenantProfile();
  const bt = BUSINESS_TYPES.find(b => b.key === profile.data?.business_type)
    ?? BUSINESS_TYPES.find(b => b.key === "other")!;
  const [name, setName] = useState("");
  const [role, setRole] = useState("");

  const create = useMutation({
    mutationFn: createStaffResource,
    onSuccess: () => {
      setName("");
      setRole("");
      void queryClient.invalidateQueries({ queryKey: queryKeys.resources.all() });
    },
  });

  const rows = roster.data?.resources ?? [];

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed || create.isPending) return;
    create.mutate({ name: trimmed, role: role.trim() || undefined });
  }

  return (
    <>
      <SectionLabel>{bt.resourceLabelPlural}</SectionLabel>
      <Panel style={{ marginBottom: 18 }}>
        {roster.isPending && <div className="py-3"><Skeleton h={52} /></div>}
        {roster.isError && (
          <div className="py-2">
            <ErrorState inline error={roster.error} onRetry={() => void roster.refetch()} />
          </div>
        )}
        {!roster.isPending && !roster.isError && rows.length === 0 && (
          <div className="py-3 text-[12.5px] text-t3">
            None yet. Add a {bt.resourceLabel.toLowerCase()} so the schedule can draw a named lane.
          </div>
        )}
        {rows.map((r, i) => (
          <div
            key={r.resource_id}
            className="flex items-center justify-between py-[11px]"
            style={{ borderBottom: i < rows.length - 1 ? "1px solid #EFEFEF" : "none" }}
          >
            <div className="min-w-0">
              <div className="text-[13.5px] font-semibold text-ink truncate">{r.name}</div>
              {r.role ? <div className="text-[11.5px] text-t3 mt-[1px]">{r.role}</div> : null}
            </div>
            {!r.active && <span className="text-[11px] text-t3">Inactive</span>}
          </div>
        ))}
        <form onSubmit={onSubmit} className="flex gap-2 mt-3">
          <input
            value={name}
            onChange={e => setName(e.target.value)}
            placeholder={`${bt.resourceLabel} name`}
            className="flex-1 h-[40px] border border-cd-border rounded-[10px] px-3 text-[13px] text-ink outline-none"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          />
          <input
            value={role}
            onChange={e => setRole(e.target.value)}
            placeholder="Role (optional)"
            className="w-[140px] h-[40px] border border-cd-border rounded-[10px] px-3 text-[13px] text-ink outline-none"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          />
          <button
            type="submit"
            disabled={!name.trim() || create.isPending}
            className="text-[12.5px] font-semibold text-white bg-ink px-3 py-1.5 rounded-[8px] border-none cursor-pointer hover:brightness-110 disabled:opacity-40"
            style={{ fontFamily: "'Inter Tight', system-ui, sans-serif" }}
          >
            Add
          </button>
        </form>
        {create.isError && (
          <div className="mt-2">
            <ErrorState inline error={create.error} title="Couldn’t add this resource." />
          </div>
        )}
      </Panel>
    </>
  );
}
