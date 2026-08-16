-- Development seed: a venue that looks like a venue.
--
-- WHY: `seed-dev-tenant.sql` seeds the minimum needed to mint a TenantContext —
-- one DID and hourly capacity for a resource literally called `svc_001`. That is
-- enough to prove the plumbing and actively misleading for everything else:
--
--   * Nothing in the database has a NAME, so a read-back says "Service: svc_001"
--     and the agent cannot describe what it just booked.
--   * `vendor_catalog_items` is empty, so `search_vendor_catalog` has nothing to
--     return and the menu tooling has never been exercised against real rows.
--   * Worst of all on voice: an identifier like `svc_001` does not survive a
--     recogniser. Spoken aloud it comes back as "SVC zero zero one", the
--     extractor faithfully records that, no capacity row matches the derived
--     slot key, and the booking consumes nothing while still reaching `booked`.
--     Speakable identifiers (`dinner`, `fitzroy`) round-trip through ASR intact.
--
-- This seed adds a plausible restaurant on top of the same tenant: a display
-- name, named sittings, named rooms, realistic opening hours, and a published
-- menu. `seed-dev-tenant.sql` is unchanged and still required first — this is
-- additive, and the `svc_001` rows it creates keep working.
--
--   docker exec -i cordia-postgres psql -U cordia -d Cordia_API \
--       < apps/api/scripts/seed-dev-tenant.sql
--   docker exec -i cordia-postgres psql -U cordia -d Cordia_API \
--       < apps/api/scripts/seed-dev-venue.sql
--
-- Idempotent: every statement is ON CONFLICT guarded, and the capacity insert
-- deliberately does NOT reset held/booked counters, so re-running never
-- resurrects consumed capacity.

BEGIN;

-- ---------------------------------------------------------------------------
-- The venue
-- ---------------------------------------------------------------------------

INSERT INTO public.tenant_display_names (tenant_id, display_name)
VALUES ('tenant_dev_001', 'Marlowe & Fig')
ON CONFLICT (tenant_id) DO UPDATE SET display_name = EXCLUDED.display_name,
                                      updated_at   = now();

-- ---------------------------------------------------------------------------
-- Capacity: named sittings, at hours a restaurant actually serves
--
-- `capacity_slots` has no service/location columns — the resource dimension is
-- carried in `resource_id` and encoded into `slot_id` by CapacitySlotKey.Derive,
-- which prefers resource_id, then service_id, then location_id. So the
-- service_id the agent captures IS the resource here, and these ids are chosen
-- to be things a person can say out loud:
--
--   breakfast  08:00-10:00   24 covers   Fitzroy dining room
--   lunch      12:00-14:00   40 covers   Fitzroy dining room
--   dinner     17:00-21:00   60 covers   Fitzroy dining room + rooftop
--
-- The timestamp half of the key is format-sensitive (spec 07 open-decision §4):
-- .NET writes `yyyy-MM-ddTHH:mm:ss.fffffffzzz` — seven fractional digits and an
-- explicit offset — and a draft carrying a different offset derives a different
-- key and falls back to the informational path. Reproduced exactly below.
-- ---------------------------------------------------------------------------

INSERT INTO public.capacity_slots (
    tenant_id, slot_id, resource_id, starts_at,
    total_capacity, held_capacity, booked_capacity, version
)
SELECT
    'tenant_dev_001',
    sitting.service_id || '|' ||
        to_char(slot_time AT TIME ZONE 'UTC',
                'YYYY-MM-DD"T"HH24:MI:SS') || '.0000000+00:00',
    sitting.service_id,
    slot_time,
    sitting.covers,
    0,
    0,
    0
FROM (
    VALUES
        ('breakfast', 8,  10, 24),
        ('lunch',     12, 14, 40),
        ('dinner',    17, 21, 60)
) AS sitting(service_id, first_hour, last_hour, covers)
CROSS JOIN LATERAL (
    SELECT generate_series(
        date_trunc('day', now() AT TIME ZONE 'UTC') + make_interval(hours => sitting.first_hour),
        date_trunc('day', now() AT TIME ZONE 'UTC') + interval '60 days'
            + make_interval(hours => sitting.last_hour),
        interval '1 hour'
    ) AS slot_time
) AS slots
-- generate_series walks every hour of every day; keep only this sitting's hours.
WHERE EXTRACT(hour FROM slot_time) BETWEEN sitting.first_hour AND sitting.last_hour
ON CONFLICT (tenant_id, slot_id) DO NOTHING;

-- ---------------------------------------------------------------------------
-- The menu
--
-- One PUBLISHED catalog — `uq_published_catalog_per_tenant` allows exactly one
-- per tenant, which is why this is upserted rather than blindly inserted. These
-- are the rows `search_vendor_catalog` (GET /api/catalog/items) reads, and the
-- trigram indexes on name/description mean a fuzzy search actually works.
--
-- `extraction_confidence` is left NULL on purpose: these items were entered, not
-- extracted from a menu artifact, and a confidence score would be a fabrication.
-- The isolated menu-extraction agent is what populates that field, and it stamps
-- what its model actually reported.
-- ---------------------------------------------------------------------------

INSERT INTO public.vendor_catalogs (
    tenant_id, catalog_id, version_label, status,
    approved_at, approved_by, source_hash, created_at, version, status_changed_at
) VALUES (
    'tenant_dev_001', 'cat_marlowe_autumn', 'autumn-2026', 'published',
    now(), 'seed@marloweandfig.test', 'seed-not-extracted', now(), 0, now()
)
ON CONFLICT (tenant_id, catalog_id) DO UPDATE
    SET status = EXCLUDED.status, version_label = EXCLUDED.version_label;

INSERT INTO public.vendor_catalog_items (
    tenant_id, item_id, catalog_id, name, description,
    price_minor, currency, category, allergen_info, menu_metadata
)
VALUES
    ('tenant_dev_001', 'itm_oysters', 'cat_marlowe_autumn',
     'Sydney Rock Oysters',
     'Half dozen, finger lime and eschalot mignonette.',
     2800, 'AUD', 'entree',
     '{"contains": ["shellfish", "sulphites"]}'::jsonb,
     '{"course": "entree", "servings": 6}'::jsonb),

    ('tenant_dev_001', 'itm_burrata', 'cat_marlowe_autumn',
     'Burrata and Heirloom Tomato',
     'Stracciatella, basil oil, aged balsamic, sourdough crisp.',
     2400, 'AUD', 'entree',
     '{"contains": ["dairy", "gluten"], "vegetarian": true}'::jsonb,
     '{"course": "entree"}'::jsonb),

    ('tenant_dev_001', 'itm_kingfish', 'cat_marlowe_autumn',
     'Hiramasa Kingfish Crudo',
     'Green chilli, cucumber, toasted rice, makrut lime.',
     2900, 'AUD', 'entree',
     '{"contains": ["fish"], "gluten_free": true}'::jsonb,
     '{"course": "entree"}'::jsonb),

    ('tenant_dev_001', 'itm_barramundi', 'cat_marlowe_autumn',
     'Crisp-Skin Barramundi',
     'Saffron fregola, fennel, salsa verde.',
     4200, 'AUD', 'main',
     '{"contains": ["fish", "gluten"]}'::jsonb,
     '{"course": "main"}'::jsonb),

    ('tenant_dev_001', 'itm_lamb', 'cat_marlowe_autumn',
     'Slow-Roast Lamb Shoulder',
     'For two. Smoked eggplant, pomegranate, flatbread.',
     8800, 'AUD', 'main',
     '{"contains": ["gluten", "sesame"]}'::jsonb,
     '{"course": "main", "serves": 2}'::jsonb),

    ('tenant_dev_001', 'itm_gnocchi', 'cat_marlowe_autumn',
     'Potato Gnocchi',
     'Roast pumpkin, sage burnt butter, pangrattato.',
     3400, 'AUD', 'main',
     '{"contains": ["gluten", "dairy", "egg"], "vegetarian": true}'::jsonb,
     '{"course": "main"}'::jsonb),

    ('tenant_dev_001', 'itm_steak', 'cat_marlowe_autumn',
     'Dry-Aged Sirloin 300g',
     'Grass-fed, 40-day aged. Bone marrow butter, watercress.',
     5600, 'AUD', 'main',
     '{"contains": ["dairy"], "gluten_free": true}'::jsonb,
     '{"course": "main", "weight_grams": 300}'::jsonb),

    ('tenant_dev_001', 'itm_greens', 'cat_marlowe_autumn',
     'Charred Greens',
     'Broccolini, almond, preserved lemon.',
     1600, 'AUD', 'side',
     '{"contains": ["nuts"], "vegan": true}'::jsonb,
     '{"course": "side"}'::jsonb),

    ('tenant_dev_001', 'itm_potatoes', 'cat_marlowe_autumn',
     'Duck Fat Potatoes',
     'Rosemary salt, garlic aioli.',
     1400, 'AUD', 'side',
     '{"contains": ["egg"], "gluten_free": true}'::jsonb,
     '{"course": "side"}'::jsonb),

    ('tenant_dev_001', 'itm_tart', 'cat_marlowe_autumn',
     'Burnt Honey Tart',
     'Creme fraiche, poached quince.',
     1800, 'AUD', 'dessert',
     '{"contains": ["dairy", "gluten", "egg"], "vegetarian": true}'::jsonb,
     '{"course": "dessert"}'::jsonb),

    ('tenant_dev_001', 'itm_affogato', 'cat_marlowe_autumn',
     'Affogato',
     'Vanilla bean gelato, single-origin espresso, optional amaro.',
     1500, 'AUD', 'dessert',
     '{"contains": ["dairy"], "vegetarian": true}'::jsonb,
     '{"course": "dessert", "alcohol_optional": true}'::jsonb),

    ('tenant_dev_001', 'itm_tasting', 'cat_marlowe_autumn',
     'Chef''s Tasting Menu',
     'Six courses. Whole table only. Dietaries with 48 hours notice.',
     14500, 'AUD', 'set_menu',
     '{"notes": "accommodates most dietaries on notice"}'::jsonb,
     '{"course": "set_menu", "courses": 6, "whole_table": true}'::jsonb)
ON CONFLICT (tenant_id, item_id) DO UPDATE
    SET name          = EXCLUDED.name,
        description   = EXCLUDED.description,
        price_minor   = EXCLUDED.price_minor,
        category      = EXCLUDED.category,
        allergen_info = EXCLUDED.allergen_info,
        menu_metadata = EXCLUDED.menu_metadata;

-- ---------------------------------------------------------------------------
-- What the venue offers, by name
--
-- The closed set `resolve_tenant_resources` matches a spoken name against. The
-- extractor may never invent an identifier, so without these rows "book dinner
-- at the Fitzroy room" is recognised perfectly and yields nothing bookable.
--
-- Aliases are a synonym list, not a fuzzy index: matching is exact after
-- reducing to lower-case alphanumerics, so anything a caller plausibly says has
-- to be spelled out here. That is the trade — a miss asks the customer, whereas
-- a fuzzy match that is wrong books the wrong thing and nothing catches it.
-- ---------------------------------------------------------------------------

INSERT INTO public.tenant_resources
    (tenant_id, kind, resource_id, name, aliases, active)
VALUES
    ('tenant_dev_001', 'service', 'breakfast', 'Breakfast',
     ARRAY['morning', 'brekkie', 'breakfast sitting'], true),
    ('tenant_dev_001', 'service', 'lunch', 'Lunch sitting',
     ARRAY['lunchtime', 'midday', 'lunch service'], true),
    ('tenant_dev_001', 'service', 'dinner', 'Dinner sitting',
     ARRAY['evening', 'supper', 'dinner service', 'evening sitting'], true),
    -- Kept so the existing runners and docs keep working verbatim.
    ('tenant_dev_001', 'service', 'svc_001', 'General booking',
     ARRAY['general', 'standard booking'], true),

    ('tenant_dev_001', 'location', 'fitzroy', 'Fitzroy dining room',
     ARRAY['fitzroy room', 'the fitzroy', 'dining room', 'main dining room',
           'downstairs'], true),
    ('tenant_dev_001', 'location', 'rooftop', 'Rooftop terrace',
     ARRAY['roof', 'the roof', 'terrace', 'upstairs', 'rooftop bar'], true),
    ('tenant_dev_001', 'location', 'loc_001', 'Main venue',
     ARRAY['main venue'], true)
ON CONFLICT (tenant_id, kind, resource_id) DO UPDATE
    SET name    = EXCLUDED.name,
        aliases = EXCLUDED.aliases,
        active  = EXCLUDED.active;

COMMIT;

-- ---------------------------------------------------------------------------
-- What was seeded
-- ---------------------------------------------------------------------------

SELECT display_name AS venue FROM public.tenant_display_names
WHERE tenant_id = 'tenant_dev_001';

SELECT resource_id AS sitting,
       count(*)    AS slots,
       min(total_capacity) AS covers,
       to_char(min(starts_at) AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI') AS first_slot
FROM public.capacity_slots
WHERE tenant_id = 'tenant_dev_001'
  AND resource_id IN ('breakfast', 'lunch', 'dinner')
GROUP BY resource_id
ORDER BY resource_id;

SELECT category, count(*) AS items
FROM public.vendor_catalog_items
WHERE tenant_id = 'tenant_dev_001'
GROUP BY category
ORDER BY category;

SELECT kind, string_agg(resource_id, ', ' ORDER BY resource_id) AS resources
FROM public.tenant_resources
WHERE tenant_id = 'tenant_dev_001'
GROUP BY kind
ORDER BY kind;
