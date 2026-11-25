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

export type MetricFilters = { unitId?: string; channel?: string; membershipType?: string; from?: string; to?: string; locale?: string };

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

/** Location and unit dashboards (spec 021 T014). Figures under the minimum group size arrive as null. */
export type ScopeRef = { kind: "unit" | "location"; key: string; names: Record<string, string> };

export type ScopeTile = { key: string; value: number | null; of: number | null; rate: number | null; groupRate: number | null; enough: boolean };

export type ScopeDashboardView = {
  scope: ScopeRef;
  from: string;
  to: string;
  minGroupSize: number;
  people: number;
  enough: boolean;
  groupShown: boolean;
  tiles: ScopeTile[];
  /** KPI tiles of a location from connected systems (spec 021 R3); null for a unit or when the package has none. */
  kpis?: KpiTileView[] | null;
};

/** A configured KPI tile (spec 021 R3): the metric a KPI feed sends, its names, and the permission a viewer also needs. */
export type KpiTileDefinition = { id: string; metric: string; names: Record<string, string>; permission: string | null };
export type KpiTileSetView = { tiles: KpiTileDefinition[]; version: number; changedAt: string | null; canChange: boolean };
export type KpiTileView = {
  id: string;
  metric: string;
  names: Record<string, string>;
  value: number | null;
  unit: string | null;
  periodStart: string | null;
  periodEnd: string | null;
  source: string | null;
  sourceTime: string | null;
  receivedAt: string | null;
};
export const MAX_KPI_TILES = 12;

export const scopeHref = (scope: Pick<ScopeRef, "kind" | "key">) =>
  scope.kind === "unit" ? `/analytics/scopes/unit/${encodeURIComponent(scope.key)}` : `/analytics/scopes/location?key=${encodeURIComponent(scope.key)}`;

/** Experience dashboards (spec 021 T015). Counts and rates under the minimum group size arrive as null. */
export type ExperienceCount = { key: string; pages: number };
export type PersonalizationCounts = { people: number; customized: number | null; reset: number | null };
export type VitalsRow = { dimension: "device" | "theme" | "unit" | "template" | "release"; group: string; samples: number; lcp: number | null; inp: number | null; cls: number | null };

export type ExperienceAnalyticsView = {
  from: string;
  to: string;
  minGroupSize: number;
  templates: { template: string; version: string; pages: number; published: number }[];
  layouts: ExperienceCount[];
  scopes: { unit: string; names: Record<string, string>; templates: ExperienceCount[] }[];
  personalization: { enabled: boolean; total: PersonalizationCounts; units: { group: string; names: Record<string, string>; counts: PersonalizationCounts }[] };
  performance: {
    samples: number;
    rows: VitalsRow[];
    flags: { pageId: string; template: string; release: number | null; deviceClass: string; samples: number; lcp: number | null; inp: number | null; cls: number | null; missed: string[] }[];
  };
  outcomes: { flow: string; started: number; completed: number; abandoned: number; completionRate: number | null; dropOffStep: string | null }[];
};

// Spec 021 T017 (US8): sentiment labels with the minimum group size; no text is ever shown.
export type SentimentCounts = { texts: number | null; positive: number | null; neutral: number | null; negative: number | null; score: number | null };
export type SentimentGroup = { group: string; names: Record<string, string>; counts: SentimentCounts };
export type SentimentView = {
  enabled: boolean;
  from: string;
  to: string;
  minGroupSize: number;
  total: SentimentCounts;
  weeks: { week: string; counts: SentimentCounts }[];
  topics: SentimentGroup[];
  units: SentimentGroup[];
  locations: SentimentGroup[];
  sources: SentimentGroup[];
  paused: { source: string; denial: string; at: string }[];
};
export type AnalyticsPolicyView = { sentiment: boolean; sentimentAvailable: boolean; canChange: boolean; version: number };

/** AI usage (spec 021 FR-016): figures read from the AI Gateway, the assistant and evaluation; null means hidden. */
export type AiUsageGroup = { group: string; requests: number | null; users: number | null };
export type AiAgentFigures = {
  agentId: string;
  name: Record<string, string>;
  runs: number | null;
  successRate: number | null;
  helpfulRate: number | null;
  gate: string | null;
  quality: { metric: string; locale: string; value: number | null }[];
};
export type AiUsageView = {
  from: string;
  to: string;
  minGroupSize: number;
  requests: number;
  users: number;
  inputTokens: number;
  outputTokens: number;
  cost: number;
  currency: string;
  rate: number;
  weeks: AiUsageGroup[];
  useCases: AiUsageGroup[];
  denied: { reason: string; requests: number }[];
  assistant: { questions: number | null; users: number | null; answerRate: number | null; citationRate: number | null; ratings: number | null; helpfulRate: number | null };
  agents: AiAgentFigures[];
};

/** Search gaps (spec 021 US3) from the spec 012 query feed: only queries five or more people searched for. */
export type SearchQueryRow = { query: string; searches: number; people: number; zeroResults: number; clicks: number };
export type SearchGapsView = {
  from: string;
  to: string;
  locale: string | null;
  minPeople: number;
  searches: number;
  zeroResults: number;
  clicked: number;
  clickThrough: number;
  queryTextHidden: boolean;
  zeroResultQueries: SearchQueryRow[];
  noClickQueries: SearchQueryRow[];
  topQueries: SearchQueryRow[];
  canAddBestBet: boolean;
  canWriteArticle: boolean;
  unitId: string | null;
  unitNames: Record<string, string> | null;
  refined: number;
  /** By the searcher's unit at search time; units under five people fold into "other" (counts null when hidden). */
  units: { key: string; names: Record<string, string> | null; searches: number | null; people: number | null; zeroResults: number | null; clicks: number | null }[];
  refinements: { from: string; to: string; searches: number; people: number }[];
};

/**
 * Knowledge, engagement, HR and security trend dashboards (spec 021 section 2): headline figures and sections in one
 * shape. Null figures are hidden behind the minimum group size; the Other row gathers groups that are too small.
 */
export type AreaKey = "knowledge" | "engagement" | "hr" | "security";
export type AreaColumn = "count" | "people" | "amount" | "rate" | "date";
export type AreaRow = {
  key: string;
  names: Record<string, string> | null;
  label: string | null;
  date: string | null;
  count: number | null;
  people: number | null;
  amount: number | null;
  rate: number | null;
};
export type AreaSection = { key: string; group: "week" | "key" | "name" | "label"; columns: AreaColumn[]; rows: AreaRow[]; link: string | null };
export type AreaView = {
  area: AreaKey;
  from: string;
  to: string;
  minGroupSize: number;
  totals: { key: string; value: number | null; share: boolean }[];
  sections: AreaSection[];
};
