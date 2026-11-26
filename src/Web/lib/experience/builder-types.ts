/** Shapes the page builder reads from the Experience API (spec 032). The builder decides nothing the API does not say. */

/** A layout region; `minSpan` and `maxSpan` say how far an editor may resize it (spec 032 R2 FR-007). */
export type LayoutRegion = { id: string; span: number; section: string; minSpan?: number | null; maxSpan?: number | null };
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
export type PageDefinition = {
  template: { id: string; version: string };
  layout: { id: string; version: string };
  variant: string;
  regions: DraftRegion[];
  /** Widths the editor chose for resizable regions, by region id (R2 FR-007). */
  spans?: Record<string, number> | null;
};
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
  /** The layout's regions with their widths and resize limits. */
  layoutRegions?: LayoutRegion[] | null;
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
  placement: { minSpan: number; maxSpan: number; singleton: boolean; source?: string | null; view?: string } | null;
  labels: Record<string, string> | null;
};

/** One placement that differs between the release in force and a submission (spec 032 R2 T023). */
export type PlacementChange = {
  placementId: string;
  componentRef: string;
  change: "added" | "removed" | "moved" | "resized" | "changed";
  fromRegion: string | null;
  toRegion: string | null;
};

export type PageDiff = {
  baseRelease: number | null;
  fromTemplate: string | null;
  toTemplate: string;
  fromVariant: string | null;
  toVariant: string;
  changes: PlacementChange[];
};

export type PageSubmission = {
  id: string;
  pageId: string;
  slug: string;
  status: "pending" | "approved" | "returned" | "withdrawn";
  revision: number;
  submittedBy: string;
  submittedAt: string;
  reason: string | null;
  decidedBy: string | null;
  decidedAt: string | null;
  comment: string | null;
  release: number | null;
  diff: PageDiff;
  /** The caller may approve or return it. */
  decide: boolean;
};

/** Whether a page goes live through approval, and its waiting and last decided submissions. */
export type PageApprovalState = { required: boolean; pending: PageSubmission | null; last: PageSubmission | null; impact?: boolean };

/** What publishing a shared artifact reaches (spec 032 R2 T024, FR-054); lists stop at 50, counts are whole. */
export type ImpactReport = {
  artifact: "page" | "template" | "theme";
  tenantWide: boolean;
  orgUnitCount: number;
  orgUnits: { id: string; names: Record<string, string> }[];
  audiences: { id: string; labels: Record<string, string> }[];
  pageCount: number;
  pages: { id: string; slug: string; orgUnitId: string | null }[];
  personalLayouts: number;
  personalLayoutsTouched: number;
};

/** A scheduled page release (spec 032 R2 T025); times are UTC, set and shown in the tenant's time zone. */
export type PageSchedule = {
  id: string;
  status: "scheduled" | "live" | "failed" | "cancelled" | "expired";
  goLiveAt: string;
  expiresAt: string | null;
  timeZone: string;
  reason: string | null;
  scheduledBy: string;
  scheduledAt: string;
  release: number | null;
  findings: string[];
};

export type PageScheduleState = { timeZone: string; current: PageSchedule | null };

/** An approval route for pages at a scope (spec 032 R2 T023, FR-051): the nearest route up the org tree applies. */
export type PageRoute = { id: string; orgUnitId: string | null; approverIds: string[]; updatedBy: string; updatedAt: string };

/** A governed fragment (R2 T027, FR-045): a reusable group of placements that every page placing it shows as it is now. */
export type Fragment = {
  id: string;
  key: string;
  reference: string;
  names: Record<string, string>;
  orgUnitId: string | null;
  ownerId: string;
  currentVersion: number;
  placements: DraftPlacement[];
  archivedAt: string | null;
  createdAt: string;
};
export type FragmentVersion = { version: number; note: string | null; createdBy: string; createdAt: string; placements: DraftPlacement[] };
export type FragmentImpact = { fragmentId: string; key: string; pageCount: number; pages: { id: string; slug: string; orgUnitId: string | null }[] };

/** A feature control (R2 T030, FR-057): a component or template switched on or off for a scope. */
export type FeatureControl = {
  id: string;
  subjectKind: "component" | "template";
  subjectId: string;
  orgUnitId: string | null;
  audienceId: string | null;
  enabled: boolean;
  version: number;
  changedBy: string;
  changedAt: string;
};
export type FeatureControlVersion = { version: number; enabled: boolean | null; changedBy: string; changedAt: string };
export type ComponentChoice = { componentId: string; enabled: boolean; name: string | null };
export type AudienceRuleSummary = { id: string; labels: Record<string, string> };

/** Component upgrades a tenant may deploy to its live pages (R2 T031, FR-033). */
export type ComponentUpgradeOption = { componentId: string; name: string; fromMajor: number; from: string; to: string; deployed: boolean };
export type ComponentDeployment = { componentId: string; from: string; to: string; deployedBy: string; deployedAt: string };
export type ComponentDeployments = { deployed: ComponentDeployment[]; available: ComponentUpgradeOption[] };
export type ComponentUpgradeResult = { from: string; to: string; upgraded: string[]; blocked: { pageId: string; placementId: string; reason: string }[] };

/** A kept draft revision (R2 T026): the last 50 saves of a page's draft. */
export type DraftRevision = { revision: number; updatedBy: string; updatedAt: string };

/** Spec 032 R3 FR-031: a component the tenant built, with its versions newest first and the live (approved) one. */
export type TenantComponentVersion = {
  version: number;
  status: "in_review" | "approved" | "rejected";
  sha256: string;
  size: number;
  hosts: string[];
  context: string[];
  events: string[];
  height: number;
  submittedAt: string;
  reviewedAt: string | null;
  reviewNote: string | null;
};

export type TenantComponent = { id: string; names: Record<string, string>; enabled: boolean; live: number | null; versions: TenantComponentVersion[] };

export type TenantComponents = { available: boolean; canManage: boolean; components: TenantComponent[] };
