/**
 * Operating hours (`GET`/`PUT /api/tenant-profile/hours`).
 *
 * OpenAPI is stale for this path — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";
import { fmtT, parseTime, TIME_SLOTS, type SettingsHour } from "../../data/console";

export type OperatingHour = SettingsHour;

export interface OperatingHoursResponse {
  hours: OperatingHour[];
}

export function getOperatingHours(signal?: AbortSignal) {
  return fetchApi<OperatingHoursResponse>("/api/tenant-profile/hours", { signal });
}

/** Owner-only. Replaces the seven-day week. */
export function setOperatingHours(hours: OperatingHour[]) {
  return fetchApi<OperatingHoursResponse>("/api/tenant-profile/hours", {
    method: "PUT",
    actor: true,
    reasonCode: "hours_update",
    body: JSON.stringify({ hours }),
  });
}

/** Minutes since midnight → `HH:mm` for the hours write. */
export function toHHmm(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

export function dayBoundsFromHours(
  hours: readonly OperatingHour[] | undefined,
  dayIdx: number
): { start: number; end: number; closed: boolean } {
  const day = hours?.[dayIdx];
  if (!day) return { start: 540, end: 1080, closed: false };
  return { start: parseTime(day.open), end: parseTime(day.close), closed: !day.on };
}

export function timeSlotsFromHours(
  hours: readonly OperatingHour[] | undefined
): { value: string; label: string }[] {
  const openDays = (hours ?? []).filter((h) => h.on);
  if (openDays.length === 0) return TIME_SLOTS;

  const start = Math.floor(Math.min(...openDays.map((h) => parseTime(h.open))) / 30) * 30;
  const end = Math.ceil(Math.max(...openDays.map((h) => parseTime(h.close))) / 30) * 30;
  const slots: { value: string; label: string }[] = [];
  for (let m = start; m <= end; m += 30) slots.push({ value: String(m), label: fmtT(m) });
  return slots.length > 0 ? slots : TIME_SLOTS;
}
