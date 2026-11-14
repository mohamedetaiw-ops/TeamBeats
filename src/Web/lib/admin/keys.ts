/**
 * Stored keys shown as words (spec 036 DLS-093, spec 006 FR-021). A key's localized name comes first; a key with no
 * name falls back to its area's name and never to the raw key, which stays available as secondary text.
 */
type Translator = {
  (key: never, values?: never): string;
  has(key: never): boolean;
};

const call = (t: Translator, key: string, values?: Record<string, string | number>) =>
  (t as unknown as (key: string, values?: Record<string, string | number>) => string)(key, values);
const has = (t: Translator, key: string) => t.has(key as never);

/** The last part of a key in words, for a key that has no name yet: `auth.sign_in_succeeded` reads "Sign in succeeded". */
export function keyInWords(key: string): string {
  const last = key.split(".").filter(Boolean).at(-1) ?? key;
  const words = last.replaceAll("_", " ").trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** The name under `prefix` for a dotted key (nested messages), or the key in words. */
export function keyName(t: Translator, prefix: string, key: string): string {
  const path = `${prefix}.${key}`;
  return has(t, path) ? call(t, path) : keyInWords(key);
}

/**
 * An audited action in words, read with the `admin` messages: the action's own label, otherwise "Change in {area}"
 * with the area it belongs to.
 */
export function auditActionName(t: Translator, action: string): string {
  const own = `auditActions.${action.replaceAll(".", "_")}`;
  if (has(t, own)) return call(t, own);
  const prefix = action.split(".")[0] ?? "";
  return call(t, "landing.change", { area: call(t, has(t, `auditAreas.${prefix}`) ? `auditAreas.${prefix}` : "auditAreas.other") });
}
