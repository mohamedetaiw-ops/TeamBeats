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

/** Campaign analytics (spec 021 T011). What people did is null under the minimum group size. */
export type AnalyticsCampaign = { id: string; name: string; siteId: string; status: string; createdAt: string; steps: number; firstSentAt: string | null };

export type CampaignCounts = {
  people: number;
  delivered: number;
  viewed: number | null;
  clicked: number | null;
  completed: number | null;
  viewedShare: number | null;
  completedShare: number | null;
};

export type CampaignStepAnalytics = {
  stepId: string;
  position: number;
  itemId: string;
  titles: Record<string, string>;
  status: string;
  at: string;
  sentAt: string | null;
  acknowledgementRequired: boolean;
  counts: CampaignCounts;
  channels: Record<string, number>;
};

export type CampaignEffort = {
  currency: string;
  authoringCost: number;
  sendCost: number;
  totalCost: number;
  perReached: number | null;
  perCompleted: number | null;
  estimatesVersion: number;
};

export type CampaignAnalyticsView = {
  campaign: AnalyticsCampaign;
  minGroupSize: number;
  total: CampaignCounts;
  channels: Record<string, number>;
  steps: CampaignStepAnalytics[];
  groups: { dimension: "unit" | "location"; group: string; names: Record<string, string>; audience: number; viewed: number; completed: number }[];
  effort: CampaignEffort | null;
};

export type CampaignComparison = { first: CampaignAnalyticsView; second: CampaignAnalyticsView };

export type EffortEstimatesView = {
  estimates: { authoringHoursPerItem: number; costPerHour: number; currency: string; costPerSend: Record<string, number> } | null;
  version: number;
  changedAt: string | null;
  canChange: boolean;
};

export const notificationChannels = ["in_app", "email", "slack", "push"] as const;

export const campaignAnalyticsHref = (id: string) => `/analytics/campaigns/${encodeURIComponent(id)}`;

/** Service analytics (spec 021 T012). Figures under the minimum group size arrive as null. */
export type AnalyticsService = { id: string; names: Record<string, string>; processKey: string; ownerOrgUnitId: string | null };

export type ServiceReasonRow = { group: string; names: Record<string, string>; total: number; counts: Record<string, number | null> };

export type ServiceAnalyticsView = {
  service: AnalyticsService;
  from: string;
  to: string;
  minGroupSize: number;
  requests: number;
  weeks: { week: string; requests: number }[];
  cycle: { closed: number; medianSeconds: number | null; p90Seconds: number | null };
  breached: number;
  breachedShare: number | null;
  rejected: number;
  rejectedShare: number | null;
  automationRate: number | null;
  steps: { step: string; decisions: number; medianSeconds: number | null; p90Seconds: number | null; automated: number; rejected: number; slow: boolean }[];
  reasons: {
    field: string;
    labels: Record<string, string>;
    options: { value: string; labels: Record<string, string> }[];
    rows: ServiceReasonRow[];
    all: ServiceReasonRow;
  }[];
  heatmapHref: string | null;
};

export const serviceAnalyticsHref = (id: string) => `/analytics/services/${encodeURIComponent(id)}`;

/** Policy compliance analytics (spec 021 T013). Counts and rates under the minimum group size arrive as null. */
export type ComplianceCounts = { audience: number; attested: number | null; overdue: number | null; rate: number | null };

export type ComplianceGroup = { group: string; names: Record<string, string>; counts: ComplianceCounts };

export type ComplianceCampaignView = {
  campaignId: string;
  policyId: string;
  version: number;
  names: Record<string, string>;
  launchedAt: string;
  deadline: string;
  daysLeft: number;
  total: ComplianceCounts;
  units: ComplianceGroup[];
  locations: ComplianceGroup[];
  trend: { day: string; rate: number }[];
};

export type ComplianceAnalyticsView = { minGroupSize: number; total: ComplianceCounts; campaigns: ComplianceCampaignView[] };

/** The spec 025 compliance view of a campaign, which keeps its own permissions, narrowed to a unit or a location. */
export const complianceReportHref = (policyId: string, campaignId: string, filter?: { unit?: string; location?: string }) => {
  const query = new URLSearchParams({ campaign: campaignId });
  if (filter?.unit) query.set("unit", filter.unit);
  if (filter?.location) query.set("location", filter.location);
  return `/policies/${encodeURIComponent(policyId)}/compliance?${query.toString()}`;
};
