import "server-only";
import { apiGet, type ApiResult } from "@/lib/api";
import type { EffectiveTheme, RenderedPage } from "./types";
import type { Catalogue, OrgUnit, PageSummary, PageView } from "./builder-types";

/** Effective configuration of the latest release for the signed-in viewer (spec 032 FR-002). */
export function renderPage(slug: string): Promise<ApiResult<RenderedPage>> {
  return apiGet<RenderedPage>(`/api/v1/experience/render?slug=${encodeURIComponent(slug)}`);
}

/** Effective theme of a scope, with content-hashed CSS URLs per mode and density (spec 033). */
export function getTheme(scope: string): Promise<ApiResult<EffectiveTheme>> {
  return apiGet<EffectiveTheme>(`/api/v1/experience/theme?scope=${encodeURIComponent(scope)}`);
}

/** The layout and template catalogue with names in the editor's language (spec 032 FR-017). */
export function getCatalogue(): Promise<ApiResult<Catalogue>> {
  return apiGet<Catalogue>("/api/v1/experience/catalogue");
}

/** Pages the signed-in editor may change; the API leaves out the rest. */
export function listPages(): Promise<ApiResult<PageSummary[]>> {
  return apiGet<PageSummary[]>("/api/v1/experience/pages");
}

/** Org units the viewer may see, for naming a page's scope. A refusal means the editor works at tenant level only. */
export function listUnits(): Promise<ApiResult<OrgUnit[]>> {
  return apiGet<OrgUnit[]>("/api/v1/org/units");
}

/** One page's draft and findings, for the builder. Not found when the page is outside the editor's scope. */
export function getPage(id: string): Promise<ApiResult<PageView>> {
  return apiGet<PageView>(`/api/v1/experience/pages/${encodeURIComponent(id)}`);
}
