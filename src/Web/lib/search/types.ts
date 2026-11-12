/** Search API shapes (spec 012, /api/v1/search). */
export const searchKinds = ["person", "content", "document", "knowledge", "policy", "service", "discussion"] as const;
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
