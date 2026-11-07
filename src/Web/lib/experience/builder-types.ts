/** Shapes the page builder reads from the Experience API (spec 032). The builder decides nothing the API does not say. */

export type LayoutRegion = { id: string; span: number; section: string };
export type CatalogueLayout = { id: string; version: string; nameKey: string; name: string; regions: LayoutRegion[]; fallback: string };

export type RegionPolicy = { allowedCategories: string[]; minPlacements: number; maxPlacements: number; protection: string; personal: boolean };
export type CatalogueTemplate = {
  id: string;
  version: string;
  state: string;
  name: string;
  description: string;
  layout: string;
  variants: string[];
  regions: { id: string; policy: RegionPolicy }[];
  /** True for a tenant template; the platform templates are false. */
  custom?: boolean;
};
export type Catalogue = { layouts: CatalogueLayout[]; templates: CatalogueTemplate[] };

export type PageSummary = {
  id: string;
  slug: string;
  orgUnitId: string | null;
  templateId: string;
  templateVersion: string;
  currentRelease: number | null;
  draftUpdatedAt: string;
  publishedAt: string | null;
};

export type OrgUnit = { id: string; parentId: string | null; names: Record<string, string>; level: number; status: string };

/** The unit's name in the viewer's language, then English, then any name it has. */
export function unitName(unit: OrgUnit, locale: string): string {
  return unit.names[locale] ?? unit.names.en ?? Object.values(unit.names)[0] ?? "";
}

export type DraftPlacement = { id: string; componentRef: string; span?: number | null; props?: Record<string, unknown> | null; visibilityRuleRef?: string | null };
export type DraftRegion = { id: string; placements: DraftPlacement[] };
export type PageDefinition = { template: { id: string; version: string }; layout: { id: string; version: string }; variant: string; regions: DraftRegion[] };
export type Finding = { code: string; severity: string; region?: string | null; placement?: string | null; property?: string | null; message?: string | null; locale?: string | null };
export type PageView = {
  id: string;
  slug: string;
  orgUnitId: string | null;
  templateId: string;
  templateVersion: string;
  currentRelease: number | null;
  eTag: string;
  draft: PageDefinition;
  findings: Finding[];
  /** A later released version of the page's tenant template, which the owner may apply with a template change. */
  newerTemplateVersion?: string | null;
  /** The page's template version is deprecated: the page keeps working and should move to another template. */
  templateDeprecated?: boolean;
  /** Name and region policies of the template version the page records. */
  templateName?: string | null;
  templateRegions?: { id: string; policy: RegionPolicy }[] | null;
};

/** A placement the new template has no fitting region for, with the regions the editor may put it in. */
export type UnplacedPlacement = { placementId: string; componentRef: string; fromRegion: string; options: string[] };
export type TemplateChange = { draft: PageDefinition; unplaced: UnplacedPlacement[]; findings: Finding[]; page: PageView | null };

/** Component categories a region may allow (spec 032 FR-012); an empty list allows any. */
export const COMPONENT_CATEGORIES = ["content", "media", "navigation", "data", "people", "services", "personal", "utility"] as const;
export const PROTECTIONS = ["optional", "mandatory", "protected"] as const;

export type TemplateDefinition = {
  id: string;
  version: string;
  state: string;
  layout: string;
  variants: string[];
  regions: { id: string; policy: RegionPolicy }[];
  names: Record<string, string> | null;
  descriptions: Record<string, string> | null;
};
export type TemplateVersion = { version: number; state: string; updatedAt: string; releasedAt: string | null };
/** A tenant template for its designers (spec 032 US5). An empty distribution is the whole organization. */
export type TenantTemplate = {
  id: string;
  basedOn: string;
  basedOnVersion: string;
  distribution: string[];
  latest: TemplateDefinition;
  versions: TemplateVersion[];
};

/** The template's name in the viewer's language, then English, then any name it has. */
export function templateName(template: TemplateDefinition, locale: string): string {
  const names = template.names ?? {};
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? template.id;
}

/** Where a published page opens. */
export function pagePath(slug: string): string {
  return `/p/${slug}`;
}

export type PropertySchema = {
  type: string;
  required?: boolean;
  default?: unknown;
  minimum?: number | null;
  maximum?: number | null;
  minLength?: number | null;
  maxLength?: number | null;
  enum?: string[] | null;
  items?: PropertySchema | null;
  maxItems?: number | null;
};

export type PickerItem = {
  componentRef: string;
  name: string;
  description: string;
  category: string;
  eligible: boolean;
  reasonKey: string | null;
  reason: string | null;
  properties: Record<string, PropertySchema> | null;
  placement: { minSpan: number; maxSpan: number; singleton: boolean } | null;
  labels: Record<string, string> | null;
};
