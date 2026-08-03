import { z } from "zod";
import type { paths } from "@cordia/api-contract";
import { fetchApi } from "../../api/client";

export type ConsentPolicyView =
  paths["/api/consent-policies"]["get"]["responses"][200]["content"]["application/json"];

export function getConsentPolicy(channel: string, signal?: AbortSignal) {
  return fetchApi<ConsentPolicyView>(
    `/api/consent-policies?channel=${encodeURIComponent(channel)}`,
    { signal }
  );
}

/**
 * Catalog items.
 *
 * `CatalogController.Search` returns `IActionResult`, so the generated contract types its
 * body as `never` — there is nothing to index into. Rather than casting a lie, the body is
 * treated as untrusted input and validated at runtime, which is exactly the rule in
 * `docs/frontend-types.md`: Zod for shapes the compiler cannot vouch for.
 *
 * (Making the action return `ActionResult<T>` would let the type flow through; that is an
 * API change outside this change's blast radius, so it is recorded as a follow-up.)
 */
const CatalogItemSchema = z
  .object({
    item_id: z.string().optional(),
    name: z.string().optional(),
    description: z.string().nullish(),
    category: z.string().nullish(),
    price_minor: z.number().nullish(),
    currency: z.string().nullish(),
    status: z.string().nullish(),
  })
  .passthrough();

const CatalogResponseSchema = z.union([
  z.array(CatalogItemSchema),
  z.object({ items: z.array(CatalogItemSchema) }).passthrough(),
]);

export type CatalogItem = z.infer<typeof CatalogItemSchema>;

/** Returns `[]` for a body that does not match, rather than crashing the settings screen. */
export function parseCatalogItems(body: unknown): CatalogItem[] {
  const parsed = CatalogResponseSchema.safeParse(body);
  if (!parsed.success) return [];
  return Array.isArray(parsed.data) ? parsed.data : parsed.data.items;
}

export async function listCatalogItems(
  query?: string,
  maxResults?: number,
  signal?: AbortSignal
): Promise<CatalogItem[]> {
  const params = new URLSearchParams();
  if (query) params.set("query", query);
  if (maxResults !== undefined) params.set("max_results", String(maxResults));

  const search = params.toString();
  const body = await fetchApi<unknown>(
    search ? `/api/catalog/items?${search}` : "/api/catalog/items",
    { signal }
  );

  return parseCatalogItems(body);
}
