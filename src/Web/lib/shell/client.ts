import "server-only";
import { apiGet, type ApiResult } from "@/lib/api";
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
  const profile = await apiGet<PreferenceProfile>("/api/v1/me/preference-profile");
  return profile.ok ? { ...defaultPreferences, ...profile.data.values } : defaultPreferences;
}
