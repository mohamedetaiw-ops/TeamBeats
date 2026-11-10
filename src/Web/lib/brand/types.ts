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
