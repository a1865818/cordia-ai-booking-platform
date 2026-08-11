-- Development seed: one tenant + one active voice ingress endpoint.
--
-- WHY: there is no HasData/seeder anywhere in the API. The channel webhooks
-- resolve a tenant from `tenant_channel_endpoints`, so with an empty table
-- POST /api/channels/telephony always returns 422 and no TenantContext can
-- ever be minted — which blocks every downstream agent flow.
--
-- Run AFTER the EF migrations have been applied (the API applies them at
-- startup in Development; see appsettings.Development.json):
--
--   psql "postgres://cordia:localdev@localhost:5432/Cordia_API" \
--        -f apps/api/scripts/seed-dev-tenant.sql
--
-- Idempotent: safe to re-run. The unique index uq_channel_signal_active is
-- filtered on status = 'active', so re-inserting the same active row would
-- otherwise conflict; ON CONFLICT on the primary key updates in place instead.
--
-- Enum literals below are the [PgName] values on the Cordia.Domain enums:
--   channel      -> TenantChannel.Voice        = 'voice'
--   signal_type  -> SignalType.PhoneNumber     = 'phone_number'
--   status       -> EndpointStatus.Active      = 'active'

INSERT INTO public.tenant_channel_endpoints (
    tenant_id,
    channel_endpoint_id,
    channel,
    signal_type,
    signal_value,
    configuration_version,
    policy_bundle_id,
    status,
    version,
    created_at
) VALUES (
    'tenant_dev_001',
    'cep_dev_voice_001',
    'voice',
    'phone_number',
    '+61400000001',
    'cfg-dev-1',
    'policy-dev-1',
    'active',
    0,
    now()
)
ON CONFLICT (tenant_id, channel_endpoint_id) DO UPDATE SET
    channel               = EXCLUDED.channel,
    signal_type           = EXCLUDED.signal_type,
    signal_value          = EXCLUDED.signal_value,
    configuration_version = EXCLUDED.configuration_version,
    policy_bundle_id      = EXCLUDED.policy_bundle_id,
    status                = EXCLUDED.status;

SELECT tenant_id, channel_endpoint_id, channel, signal_type, signal_value, status
FROM public.tenant_channel_endpoints
WHERE tenant_id = 'tenant_dev_001';

-- ---------------------------------------------------------------------------
-- First-party capacity for POST /api/availability/candidates.
--
-- The handler derives the slot key with Cordia.Domain.Capacity.CapacitySlotKey:
--
--     "{resource}|{startsAt:O}"
--
-- where `resource` is the first non-empty of resource_id / service_id /
-- location_id on the draft, and `:O` is .NET's round-trip format
-- (yyyy-MM-ddTHH:mm:ss.fffffffzzz — SEVEN fractional digits and an explicit
-- offset). The rows below reproduce that exactly for `svc_001` at every hour
-- for the next 60 days, expressed with a +00:00 offset.
--
-- NOT required for the text E2E: when no row matches the derived key the tenant
-- simply has no first-party inventory for that resource/time, and the handler
-- answers `status=available` / `mode=informational` with the requested time as a
-- candidate (advisory wording only, Inv-16) — the same non-tracked path
-- ReserveCapacityCommandHandler takes. These rows exist so the tracked
-- `live` / `held` branches are exercisable end to end.
--
-- Offset sensitivity is inherent to the slot-key format (spec 07 open-decision
-- §4): a draft whose starts_at carries, say, a +10:00 offset produces a
-- different key than the same instant at +00:00 and falls back to the
-- informational path. Widening the key is a spec-07 decision, not a seed
-- concern.
--
-- Idempotent: ON CONFLICT on the primary key leaves any existing held/booked
-- counters alone so re-running the seed never resurrects consumed capacity.

INSERT INTO public.capacity_slots (
    tenant_id,
    slot_id,
    resource_id,
    starts_at,
    total_capacity,
    held_capacity,
    booked_capacity,
    version
)
SELECT
    'tenant_dev_001',
    'svc_001|' || to_char(slot_start AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US') || '0+00:00',
    'svc_001',
    slot_start,
    20,
    0,
    0,
    0
FROM generate_series(
    (date_trunc('day', now() AT TIME ZONE 'UTC')) AT TIME ZONE 'UTC',
    (date_trunc('day', now() AT TIME ZONE 'UTC') + interval '60 days') AT TIME ZONE 'UTC',
    interval '1 hour'
) AS slot_start
ON CONFLICT (tenant_id, slot_id) DO NOTHING;

SELECT count(*) AS seeded_capacity_slots
FROM public.capacity_slots
WHERE tenant_id = 'tenant_dev_001';
