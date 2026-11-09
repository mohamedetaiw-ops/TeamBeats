/** Shapes of the spec 011 documents and knowledge base API, as the browser reads them. */
export type Names = Record<string, string>;

export type Rights = { view: boolean; contribute: boolean; manage: boolean };

export type LibrarySummary = {
  id: string;
  names: Names;
  scope: string;
  scopeId: string | null;
  orgUnitId: string | null;
  rights: Rights;
};

export type FieldOption = { value: string; names: Names };

export type FieldKind = "text" | "number" | "date" | "choice" | "boolean";

export type FieldDefinition = {
  key: string;
  names: Names;
  kind: FieldKind;
  required: boolean;
  default?: unknown;
  options?: FieldOption[] | null;
};

export type DocumentType = {
  id: string;
  names: Names;
  fields: FieldDefinition[];
  status: string;
  version: number;
  controlled?: { numberPrefix: string | null } | null;
};

export type LibraryView = {
  id: string;
  names: Names;
  scope: string;
  effectiveExtensions: string[];
  effectiveMaxFileMb: number;
  classificationDefault: string;
  status: string;
  types: DocumentType[];
  rights: Rights;
};

export type FolderView = {
  id: string;
  parentId: string | null;
  name: string;
  inherits: boolean;
  rights: Rights;
};

export type ItemQuery = {
  folderId?: string | null;
  recursive?: boolean;
  typeId?: string | null;
  text?: string | null;
  filters?: Record<string, string> | null;
  sort?: string | null;
  descending?: boolean;
};

export type SavedView = { id: string; names: Names; query: ItemQuery };

export type ItemSummary = {
  id: string;
  name: string;
  folderId: string | null;
  typeId: string | null;
  metadata: Record<string, unknown>;
  classification: string;
  versionLabel: string | null;
  state: string | null;
  scanState: string;
  checkedIn: boolean;
  checkedOutBy: string | null;
  updatedAt: string;
};

export type ItemPage = { items: ItemSummary[]; total: number };

export type VersionView = {
  id: string;
  major: number;
  minor: number;
  label: string;
  state: string;
  scanState: string;
  size: number;
  contentType: string;
  fileName: string;
  note: string | null;
  authorUserId: string;
  createdAt: string;
};

export type ControlledView = {
  number: string | null;
  release: "none" | "in_approval" | "scheduled";
  upcoming: VersionView | null;
  reviewDueAt: string | null;
  readConfirmationRequired: boolean;
  readConfirmedAt: string | null;
};

export type DocumentView = {
  id: string;
  libraryId: string;
  folderId: string | null;
  typeId: string | null;
  name: string;
  number: string | null;
  metadata: Record<string, unknown>;
  classification: string;
  ownerUserId: string;
  checkedIn: boolean;
  checkedOutBy: string | null;
  checkedOutAt: string | null;
  complete: boolean;
  current: VersionView | null;
  effective: VersionView | null;
  pending: VersionView | null;
  inherits: boolean;
  rights: Rights;
  version: number;
  controlled?: ControlledView | null;
};

export type PreviewView = {
  kind: "pdf" | "image" | "video" | "text" | "unavailable";
  url: string | null;
  contentType: string | null;
  expiresAt: string | null;
  reason: string | null;
};

export type TokenView = { token: string; url: string; expiresAt: string };

export const documentHref = (id: string) =>
  `/documents/${encodeURIComponent(id)}`;
export const libraryHref = (id: string) =>
  `/documents/libraries/${encodeURIComponent(id)}`;
export const articleHref = (id: string) => `/kb/${encodeURIComponent(id)}`;

/** The name in the reader's language, else English, else the first one given. */
export function nameIn(names: Names, locale: string): string {
  return names[locale] ?? names.en ?? Object.values(names)[0] ?? "";
}

export type KbTemplate = {
  key: "sop" | "howto" | "faq" | "troubleshooting";
  sections: string[];
};

export type ArticleSummary = {
  id: string;
  template: string | null;
  titles: Names;
  ownerId: string | null;
  version: number;
  publishedAt: string;
  reviewDueAt: string | null;
};

export type ArticleSection = {
  anchor: string;
  level: number;
  heading: string;
  text: string;
  link: string;
};

export type FeedbackScore = {
  helpful: number;
  notHelpful: number;
  percent: number | null;
};

export type MyFeedback = {
  helpful: boolean;
  comment: string | null;
  version: number;
  at: string;
};

export type ArticleView = {
  id: string;
  template: string | null;
  locale: string;
  titles: Names;
  ownerId: string | null;
  version: number;
  publishedAt: string;
  reviewDueAt: string | null;
  link: string;
  sections: ArticleSection[];
  relatedArticles: { id: string; titles: Names }[];
  relatedDocuments: { id: string; name: string; number: string | null }[];
  myFeedback: MyFeedback | null;
  canManage: boolean;
  score: FeedbackScore | null;
};

export type FeedbackReport = {
  score: FeedbackScore;
  comments: {
    helpful: boolean;
    comment: string;
    version: number;
    at: string;
  }[];
};
