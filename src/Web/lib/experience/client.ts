import "server-only";
import { apiGet, type ApiResult } from "@/lib/api";
import type { EffectiveTheme, RenderedPage } from "./types";

/** Effective configuration of the latest release for the signed-in viewer (spec 032 FR-002). */
export function renderPage(slug: string): Promise<ApiResult<RenderedPage>> {
  return apiGet<RenderedPage>(`/api/v1/experience/render?slug=${encodeURIComponent(slug)}`);
}

/** Effective theme of a scope, with content-hashed CSS URLs per mode and density (spec 033). */
export function getTheme(scope: string): Promise<ApiResult<EffectiveTheme>> {
  return apiGet<EffectiveTheme>(`/api/v1/experience/theme?scope=${encodeURIComponent(scope)}`);
}
