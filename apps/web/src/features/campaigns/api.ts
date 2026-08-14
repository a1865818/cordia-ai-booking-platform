/**
 * Outbound campaigns (`GET`/`POST /api/campaigns` and approve/reject/schedule).
 *
 * OpenAPI is stale for these paths — local interfaces, not `@cordia/api-contract`.
 */

import { fetchApi } from "../../api/client";
import type { Campaign, Channel } from "../../data/console";

export interface CampaignRow {
  id: string;
  title: string;
  status: string;
  audience: string;
  channel: string;
  recipients: number;
  when: string;
  message: string;
  approved: boolean;
}

export interface CampaignListResponse {
  campaigns: CampaignRow[];
}

export interface CreateCampaignInput {
  title: string;
  audience: string;
  channel: string;
  message: string;
  recipients?: number;
}

export function listCampaigns(signal?: AbortSignal) {
  return fetchApi<CampaignListResponse>("/api/campaigns", { signal });
}

/** Owner-only. */
export function createCampaign(input: CreateCampaignInput) {
  const body: Record<string, unknown> = {
    title: input.title,
    audience: input.audience,
    channel: input.channel,
    message: input.message,
  };
  if (input.recipients !== undefined) body.recipients = input.recipients;

  return fetchApi<CampaignRow>("/api/campaigns", {
    method: "POST",
    actor: true,
    reasonCode: "campaign_create",
    body: JSON.stringify(body),
  });
}

export function approveCampaign(id: string) {
  return fetchApi<CampaignRow>(`/api/campaigns/${encodeURIComponent(id)}/approve`, {
    method: "POST",
    actor: true,
    reasonCode: "campaign_approve",
  });
}

export function rejectCampaign(id: string) {
  return fetchApi<CampaignRow>(`/api/campaigns/${encodeURIComponent(id)}/reject`, {
    method: "POST",
    actor: true,
    reasonCode: "campaign_reject",
  });
}

export function scheduleCampaign(id: string, sendAt: string) {
  return fetchApi<CampaignRow>(`/api/campaigns/${encodeURIComponent(id)}/schedule`, {
    method: "POST",
    actor: true,
    reasonCode: "campaign_schedule",
    body: JSON.stringify({ send_at: sendAt }),
  });
}

const CHANNEL_ALIAS: Record<string, Channel> = {
  voice: "phone",
  phone: "phone",
  messaging: "sms",
  sms: "sms",
  whatsapp: "whatsapp",
  instagram: "instagram",
  web: "web",
  email: "email",
};

export function toConsoleCampaign(row: CampaignRow): Campaign & { approved: boolean } {
  const channel = CHANNEL_ALIAS[row.channel] ?? "web";
  const status: Campaign["status"] =
    row.status === "scheduled" || row.status === "sent" || row.status === "rejected"
      ? row.status
      : "awaiting";
  return {
    id: row.id,
    title: row.title,
    status,
    audience: row.audience,
    channel,
    recipients: row.recipients,
    when: row.when,
    message: row.message,
    approved: row.approved,
  };
}
