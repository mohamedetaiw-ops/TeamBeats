/** Brand manager contracts (spec 033 T011), as the Experience API returns them. */
export type TokenValue = { $value: string; $modes?: Record<string, string> };

export type TokenSchemaEntry = {
  name: string;
  group: string;
  type: string;
  cssName: string;
  light: string;
  dark: string | null;
  compact: string | null;
  script: string | null;
  modes: string[];
};

export type TokenSchema = {
  tokens: TokenSchemaEntry[];
  assetSlots: string[];
  fonts: Record<string, string[]>;
  easings: string[];
  /** Allowed keywords of the choice tokens, such as `design.preset` and `layout.navigation`. */
  choices: Record<string, string[]>;
  designs: DesignOption[];
};

/** One of the three TeamBeats designs with its resolved custom properties per mode (spec 036). */
export type DesignOption = { key: string; navigation: string; preview: Record<string, Record<string, string>> };

export type ContrastFinding = {
  code: string;
  severity: "blocking" | "warning";
  token: string;
  against: string;
  mode: string;
  ratio: number;
  required: number;
};

export type BrandSummary = { id: string; name: string; currentVersion: number; seeded: boolean; assignedScopes: string[] };

export type BrandView = {
  id: string;
  name: string;
  version: number;
  seeded: boolean;
  strictContrast: boolean;
  tokens: Record<string, TokenValue>;
  assets: Record<string, string>;
  assetUrls: Record<string, string>;
  findings: ContrastFinding[];
};

export type BrandCheck = { findings: ContrastFinding[]; preview: Record<string, Record<string, string>> };

export type AssetView = { reference: string; url: string; contentType: string; bytes: number };

export type ProblemErrors = Record<string, { key: string; message: string }[]>;

/** The brand's working draft, or the version in force when there is none (spec 033 R2 T014). */
export type BrandDraft = {
  brandId: string;
  name: string;
  tokens: Record<string, TokenValue>;
  assets: Record<string, string>;
  strictContrast: boolean;
  baseVersion: number;
  currentVersion: number;
  hasDraft: boolean;
  stale: boolean;
  updatedBy: string | null;
  updatedAt: string | null;
  findings: ContrastFinding[];
};

export type BrandRelease = {
  version: number;
  current: boolean;
  publishedBy: string | null;
  publishedAt: string;
  reason: string | null;
  rollbackOf: number | null;
  how: "direct" | "approved" | "scheduled" | "reverted" | null;
};

export type BrandSchedule = {
  id: string;
  status: "scheduled" | "live" | "failed" | "cancelled";
  goLiveAt: string;
  timeZone: string;
  reason: string | null;
  scheduledBy: string;
  scheduledAt: string;
  version: number | null;
  failure: string | null;
};

export type BrandScheduleState = { timeZone: string; current: BrandSchedule | null };

export type BrandSubmission = {
  id: string;
  brandId: string;
  name: string;
  status: "pending" | "approved" | "returned" | "withdrawn";
  baseVersion: number;
  submittedBy: string;
  submittedAt: string;
  reason: string | null;
  decidedAt: string | null;
  comment: string | null;
  version: number | null;
  changes: { token: string; change: "added" | "removed" | "changed" }[];
  assetChanges: string[];
  decide: boolean;
};

export type BrandApprovalState = { publish: boolean; pending: BrandSubmission | null; last: BrandSubmission | null };

/** What publishing a brand's draft reaches (spec 033 R2 T015): the spec 032 impact report plus what only themes have. */
export type BrandImpact = {
  report: import("@/lib/experience/builder-types").ImpactReport;
  changes: string[];
  excluded: { id: string; names: Record<string, string> }[];
  overridden: { scope: string; scopeNames: Record<string, string>; token: string; value: TokenValue }[];
  previewScope: string;
  previewNames: Record<string, string>;
  preview: Record<string, Record<string, string>>;
};
