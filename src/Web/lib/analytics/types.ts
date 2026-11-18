/** Item analytics as the API returns them (spec 021 US1). Groups under the minimum size are already merged into "other". */
export type ItemTrendDay = { day: string; views: number; readers: number };

export type ItemGroupView = { group: string; names: Record<string, string>; audience: number; readers: number; acknowledged: number | null; reach: number };

export type ItemAnalyticsView = {
  source: string;
  itemId: string;
  titles: Record<string, string>;
  liveFrom: string | null;
  audience: number;
  readers: number;
  reach: number;
  acknowledgementRequired: boolean;
  acknowledged: number | null;
  acknowledgedShare: number | null;
  reactions: number;
  comments: number;
  minGroupSize: number;
  trend: ItemTrendDay[];
  groups: ItemGroupView[];
};

export type AnalyticsSource = "content" | "policies";

export const itemAnalyticsApi = (source: AnalyticsSource, id: string) => `/api/v1/analytics/items/${source}/${encodeURIComponent(id)}`;

export const otherGroup = "other";
export const noUnitGroup = "none";

/** Adoption basics (spec 021 T007). People counts under the minimum group size come back as null. */
export type AdoptionView = {
  minGroupSize: number;
  weeks: { week: string; activeUsers: number }[];
  unitsWeek: string | null;
  units: { group: string; names: Record<string, string>; activeUsers: number }[];
  personalization: { week: string; people: number | null; customized: number | null; reset: number | null }[];
  templates: { templateId: string; templateVersion: string; pages: number; published: number }[];
};

/** The support list of frontend errors (spec 021 US9 scenario 6): no messages or payloads, only what support needs to look a failure up. */
export type FrontendErrorsView = {
  days: number;
  release: string | null;
  releases: { release: string; count: number; lastAt: string }[];
  groups: {
    release: string;
    kind: "render" | "boundary" | "network" | "chunk";
    component: string | null;
    area: string | null;
    count: number;
    firstAt: string;
    lastAt: string;
    correlationIds: string[];
  }[];
};

/** The analytics workspace (spec 021 T010): the dashboards the package opens, the units the viewer may pick and the metrics. */
export type WorkspaceMetric = { key: string; measure: "people" | "views"; filters: string[]; groupBy: string[] };

export type WorkspaceView = {
  dashboards: { key: string; capability: string; metrics: string[] }[];
  wholeTenant: boolean;
  units: { id: string; names: Record<string, string> }[];
  minGroupSize: number;
  channels: string[];
  membershipTypes: string[];
  metrics: WorkspaceMetric[];
};

export type MetricFilters = { unitId?: string; channel?: string; membershipType?: string; from?: string; to?: string };

/** A metric answer. Suppressed selections carry no figures; a week under the minimum group size is null. */
export type MetricResult = {
  metric: string;
  groupBy: string | null;
  from: string;
  to: string;
  minGroupSize: number;
  suppressed: boolean;
  total: number | null;
  weeks: { week: string; value: number | null }[];
  groups: { group: string; names: Record<string, string>; value: number }[];
};

export const analyticsQueryApi = "/api/v1/analytics/query";
