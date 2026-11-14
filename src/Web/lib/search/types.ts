/** Search API shapes (spec 012, /api/v1/search). */
export const searchKinds = ["person", "content", "document", "knowledge", "policy", "service", "discussion", "course"] as const;
export type SearchKind = (typeof searchKinds)[number];

export type SearchResult = {
  itemId: string;
  source: string;
  kind: SearchKind;
  title: string;
  snippet: string;
  link: string;
  locale: string;
  updatedAt: string | null;
  recommended: boolean;
};

export type SearchPage = {
  query: string;
  total: number;
  more: boolean;
  page: number;
  pageSize: number;
  results: SearchResult[];
  facets: Partial<Record<SearchKind, number>>;
  degraded: boolean;
  alternatives: SearchResult[];
};

export type Suggestion = { itemId: string; source: string; kind: SearchKind; title: string; link: string };

export type BestBet = {
  id: string;
  query: string;
  locale: string | null;
  source: string;
  itemId: string;
  kind: SearchKind | null;
  title: string | null;
  createdAt: string;
};

export type SynonymSet = { id: string; locale: string | null; terms: string[]; updatedAt: string };

export type Rebuild = { id: string; requestedAt: string; startedAt: string | null; completedAt: string | null; items: number; units: number };

export const isSearchKind = (value: string | undefined): value is SearchKind => !!value && (searchKinds as readonly string[]).includes(value);

/** Retrieval configuration (spec 012 US3 scenario 3, FR-008); version 0 is the platform default. */
export type RetrievalSettings = { version: number; passageTokens: number; overlapPercent: number; topK: number; embeddingModel: string | null };

export type RetrievalChangeStatus = "building" | "gating" | "held" | "active" | "rejected" | "cancelled" | "superseded";

export type RetrievalChange = {
  id: string;
  settings: RetrievalSettings;
  status: RetrievalChangeStatus;
  proposedAt: string;
  builtAt: string | null;
  shadowUnits: number | null;
  gateResult: "passed" | "failed" | "incomplete" | "failed_critical" | null;
  failures: string[];
  decidedAt: string | null;
  overrideReason: string | null;
  appliedAt: string | null;
  canApply: boolean;
  canCancel: boolean;
};

export type RetrievalConfig = { active: RetrievalSettings; open: RetrievalChange | null; history: RetrievalChange[] };

/** Federated search (spec 012 US5): results asked of an external system live, as the person, never stored. */
export type FederatedHit = {
  title: string;
  link: string;
  summary: string | null;
  location: string | null;
  mediaType: string | null;
  modifiedAt: string | null;
};
export type FederatedState =
  | "ok"
  | "not_connected"
  | "reconnect"
  | "unavailable"
  | "timeout";
export type FederatedGroup = {
  source: string;
  state: FederatedState;
  results: FederatedHit[];
};
export type FederatedPage = { query: string; groups: FederatedGroup[] };
export type FederatedSource = {
  source: string;
  enabled: boolean;
  updatedAt: string | null;
};

/** The AI summary above results (spec 012 US6): off, or what the assistant made of the top permitted passages. */
export type SummaryCitation = {
  n: number;
  title: string;
  link: string;
  kind: string;
};
export type SearchSummaryView = {
  state: "off" | "answered" | "none" | "withheld" | "unavailable";
  turnId: string | null;
  text: string | null;
  citations: SummaryCitation[];
};
export type AiSummarySetting = { enabled: boolean; updatedAt: string | null };
