/**
 * State that is only valid for the thing it was captured for.
 *
 * Two defects in this console had the same shape: a value outlived the context it belonged to
 * for at least one render, and something irreversible happened in that window.
 *
 *  - The schedule grid kept the **previous week's** bookings while the header had already moved
 *    to the next week, because the new window's query starts at `data === undefined` and the
 *    rows lived in an untagged array. The operator read real bookings under wrong dates — the
 *    exact defect `week.ts` exists to prevent.
 *  - The transcript reader kept the **previous conversation's** reason code and step-up grant
 *    for one render after the open session changed, because they were cleared in a `useEffect`
 *    that runs *after* react-query has already re-subscribed and fetched. That fired an audited
 *    sensitive read the operator never authorised, carrying a grant issued for someone else
 *    (spec 14 §2, Inv-11).
 *
 * Both are fixed the same way and for the same reason: store the owner alongside the value and
 * compare at **render**, so there is no frame in which the two disagree. Clearing in an effect
 * cannot fix either — an effect is by definition too late.
 */

/** A value together with the key it was captured for. */
export interface ScopedTo<TKey, TValue> {
  key: TKey;
  value: TValue;
}

/**
 * The value if it belongs to `key`, otherwise `fallback`.
 *
 * Deliberately total and synchronous: it is meant to be called during render.
 */
export function valueForKey<TKey, TValue>(
  scoped: ScopedTo<TKey, TValue>,
  key: TKey,
  fallback: TValue
): TValue {
  return scoped.key === key ? scoped.value : fallback;
}

/**
 * Applies an update, re-basing onto `fallback` when the stored value belongs to a different key.
 *
 * The re-basing matters as much as the read: an optimistic write that resolves after the operator
 * has moved on must not resurrect the old context's data by merging into it.
 */
export function updateForKey<TKey, TValue>(
  scoped: ScopedTo<TKey, TValue>,
  key: TKey,
  fallback: TValue,
  update: TValue | ((current: TValue) => TValue)
): ScopedTo<TKey, TValue> {
  const base = scoped.key === key ? scoped.value : fallback;
  return {
    key,
    value: typeof update === "function" ? (update as (c: TValue) => TValue)(base) : update,
  };
}
