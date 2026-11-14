import "server-only";
import { apiGet, type ApiResult } from "@/lib/api";
import { getTenantTheme } from "@/lib/experience/client";
import { designOf } from "@/lib/experience/design";
import { defaultPreferences, type PreferenceKey, type PreferenceProfile, type Resolved, type Shell } from "./types";

/** The live shell for the signed-in viewer: layout, menus with permitted links only, and their organizations (spec 008). */
export function getShell(): Promise<ApiResult<Shell>> {
  return apiGet<Shell>("/api/v1/experience/shell");
}

/** Maps an address to a page, a redirect or the recovery answer, which is the same for missing and not permitted. */
export function resolvePath(path: string): Promise<ApiResult<Resolved>> {
  return apiGet<Resolved>(`/api/v1/experience/resolve?path=${encodeURIComponent(path)}`);
}

/** The viewer's appearance preferences, or the defaults when signed out or unavailable. */
export async function getPreferences(): Promise<Record<PreferenceKey, string>> {
  const [profile, theme] = await Promise.all([apiGet<PreferenceProfile>("/api/v1/me/preference-profile"), getTenantTheme()]);
  if (!profile.ok) return defaultPreferences;
  const values = { ...defaultPreferences, ...profile.data.values };
  // A person who never chose a density gets the density their design declares (spec 033 FR-032); a saved choice wins.
  if (!profile.data.chosen?.includes("density") && theme.ok) values.density = designOf(theme.data.variants).density;
  return values;
}
