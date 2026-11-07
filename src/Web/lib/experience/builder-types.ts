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
};

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
