/** Spec 009 content: shapes returned by `/api/v1/content/*`. The body is a block document, never HTML. */
export type Inline = { text: string; marks?: string[] | null; link?: string | null; mention?: string | null };

export type Block = {
  type: "paragraph" | "heading" | "list" | "quote" | "table" | "image" | "link" | "embed";
  children?: Inline[] | null;
  level?: number | null;
  ordered?: boolean | null;
  items?: Inline[][] | null;
  rows?: Inline[][][] | null;
  assetId?: string | null;
  alt?: string | null;
  url?: string | null;
  text?: string | null;
};

export type BodyDocument = { blocks: Block[] };

export type FeedItem = {
  id: string;
  siteId: string;
  type: string;
  locale: string;
  fallback: boolean;
  title: string;
  summary: string | null;
  publishedAt: string;
  pinned: boolean;
  featured: boolean;
  readMinutes: number;
  reactions: number;
};

export type FeedPage = { items: FeedItem[]; next: string | null };

export type ReaderView = {
  id: string;
  siteId: string;
  type: string;
  locale: string;
  fallback: boolean;
  locales: string[];
  title: string;
  summary: string | null;
  body: BodyDocument;
  version: number;
  publishedAt: string;
  updatedAt: string;
  expired: boolean;
  pinned: boolean;
  featured: boolean;
  underReview: boolean;
  /** The anchor of each heading in order, for citation links. */
  anchors?: string[] | null;
  /** The item's site is archived: still readable by link, out of feeds (spec 009 US6 scenario 5). */
  siteArchived?: boolean;
  /** The hashtags of the live version (spec 009 US9 scenario 3), each opening its topic page. */
  tags?: string[] | null;
};

export type CriticalNotice = {
  id: string;
  version: number;
  presentation: "modal" | "banner";
  requireAck: boolean;
  dismissible: boolean;
  remindLaterLeft: number;
  deadlineAt: string | null;
  locale: string;
  title: string;
  summary: string | null;
  body: BodyDocument;
  publishedAt: string;
};

export type PendingCritical = { modal: CriticalNotice | null; banners: CriticalNotice[] };

export type ContentPerson = { id: string; displayName: string };

export type CommentView = {
  id: string;
  itemId: string;
  author: ContentPerson;
  body: string;
  status: "visible" | "held" | "hidden" | "deleted";
  createdAt: string;
  mine: boolean;
  reported: boolean;
  mentions?: ContentPerson[] | null;
};

export type CommentPage = { items: CommentView[]; commentsOn: boolean; moderator: boolean };

export const reactionKinds = ["like", "celebrate", "support", "insightful"] as const;
export type ReactionKind = (typeof reactionKinds)[number];

export type ReactionSummary = { itemId: string; counts: Record<ReactionKind, number>; mine: ReactionKind | null };

/** Where a reader opens an item. */
export const readHref = (id: string) => `/content/read/${encodeURIComponent(id)}`;

/** Authoring shapes of `/api/v1/content/items` and `/api/v1/content/sites` (spec 009 T023). */
export type UnitTarget = { id: string; subtree?: boolean };
export type AudienceClause = { orgUnits?: UnitTarget[] | null; membershipTypes?: string[] | null; locations?: string[] | null; groups?: string[] | null; roles?: string[] | null };
export type ContentAudience = { anyOf?: AudienceClause[] | null };

export type ContentVariant = { title: string; summary: string | null; body: BodyDocument; outOfDate: boolean; machineTranslated?: boolean };

export type CriticalSettings = { presentation: "modal" | "banner"; requireAck: boolean; remindLaterMax: number; deadlineAt: string | null; dismissible: boolean };

export type ItemView = {
  id: string;
  siteId: string;
  type: string;
  status: string;
  classification: string;
  primaryLocale: string;
  audience: ContentAudience;
  owner: ContentPerson | null;
  backupOwner: ContentPerson | null;
  reviewDueAt: string | null;
  pinned: boolean;
  featured: boolean;
  publishedVersion: number | null;
  draftVersion: number | null;
  publishAt: string | null;
  expiresAt: string | null;
  publishedAt: string | null;
  expiredAt: string | null;
  variants: Record<string, ContentVariant>;
  changeNote: string | null;
  createdBy: ContentPerson;
  updatedAt: string;
  version: number;
  inReview: boolean;
  critical: CriticalSettings | null;
  ownerReviewOpen: boolean;
  retirePending: boolean;
};

/** An approval route of one content type (spec 009 FR-003): one step with approvers, or up to five steps in order. */
export type ContentRouteStep = { approvers?: string[] | null };
export type ContentRoute = { approval: boolean; approvers?: string[] | null; steps?: ContentRouteStep[] | null };
export const maxRouteSteps = 5;

export type SiteView = {
  id: string;
  names: Record<string, string>;
  orgUnitId: string | null;
  timeZone: string;
  commentsMode: string;
  moderation: boolean;
  isDefault: boolean;
  status: string;
  version: number;
  defaultAudience: ContentAudience;
  routes?: Record<string, ContentRoute>;
  homePageId: string | null;
  homeSlug: string | null;
  templateKey: string | null;
  sharedAudience: ContentAudience | null;
  archivedAt: string | null;
  canManage: boolean;
};

/** A site as a reader's navigation lists it (spec 009 US6 scenario 2); the home path is set once its page is published. */
export type SiteLink = { id: string; names: Record<string, string>; homeSlug: string | null };
export type SiteTemplate = { key: string; pageTemplate: string; commentsMode: string; moderation: boolean; newsApproval: boolean };
export const siteTemplates = ["subsidiary", "location", "brand"] as const;

export type VersionSummary = {
  number: number;
  author: ContentPerson;
  approver: ContentPerson | null;
  changeNote: string | null;
  material: boolean;
  locales: string[];
  createdAt: string;
  publishedAt: string | null;
  current: boolean;
  draft: boolean;
  restoredFrom: number | null;
};

export type DiffSegment = { op: "equal" | "added" | "removed"; text: string };
export type VersionCompare = { from: number; to: number; locale: string; title: DiffSegment[]; summary: DiffSegment[]; body: DiffSegment[] };

export type AcknowledgementSummary = { itemId: string; countedFrom: number; audience: number; confirmed: number; pending: number; byChannel: Record<string, number>; deadlineAt: string | null };
export type TeamMemberStatus = { person: ContentPerson; confirmed: boolean; at: string | null; channel: string | null };
export type AckReportView = { id: string; itemId: string; status: string; rowCount: number | null; createdAt: string; completedAt: string | null; expiresAt: string | null };

export type SiteHealth = {
  siteId: string;
  names: Record<string, string>;
  overdueForReview: number;
  withoutActiveOwner: number;
  expired: number;
  notViewed: number | null;
  /** Nobody holds content administration at the site's unit (spec 009 section 4). */
  ownerless?: boolean;
  archived?: boolean;
  /** Items of an archived site still live and readable by link. */
  liveLinks?: number;
};

/** Content type settings with retention (spec 009 FR-020): both retention fields set, or both null to keep everything. */
export type ContentTypeSettings = {
  key: string;
  requiresOwner: boolean;
  reviewMonths: number | null;
  showHistoryToReaders: boolean;
  underReviewLabel: boolean;
  retentionMonths?: number | null;
  retentionAction?: "versions" | "item" | null;
};
export type ContentSettingsView = { embedHosts: string[]; types: ContentTypeSettings[] };

export type ScreenView = { id: string; siteId: string; name: string; createdAt: string; rotatedAt: string | null };
export type ScreenWithAddress = { screen: ScreenView; path: string };
export type SignageFeed = { name: string; siteNames: Record<string, string>; items: { id: string; locale: string; title: string; summary: string | null; publishedAt: string }[] };
export type HealthItem = { id: string; siteId: string; type: string; status: string; title: string; ownerId: string | null; reviewDueAt: string | null; expiredAt: string | null };

export const contentTypes = ["news", "announcement", "page"] as const;
export const contentStatuses = ["draft", "in_review", "approved", "scheduled", "published", "expired", "archived"] as const;
export const classifications = ["public", "internal", "confidential", "restricted"] as const;
export const membershipTypes = ["employee", "contractor", "guest"] as const;

/** A name in the viewer's language, then English, then any. */
export const pickName = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** The variant to show first: the viewer's language, then the primary one, then any. */
export function variantFor(item: Pick<ItemView, "variants" | "primaryLocale">, locale: string): ContentVariant | undefined {
  return item.variants[locale] ?? item.variants[item.primaryLocale] ?? Object.values(item.variants)[0];
}

export const editHref = (id: string) => `/content/items/${encodeURIComponent(id)}`;

/** What a person follows (spec 009 FR-017): a site by ID or a topic by its tag. */
export type FollowView = { kind: "site" | "topic"; key: string; at: string };
export const topicHref = (tag: string) => `/content/topics/${encodeURIComponent(tag)}`;

/** Campaigns across channels (spec 009 US8): steps of one site's items sent on a schedule to one audience. */
export type CampaignStepView = {
  id: string;
  position: number;
  itemId: string;
  itemTitle: Record<string, string>;
  at: string;
  mandatory: boolean;
  onlyNotActed: boolean;
  status: "pending" | "sent" | "skipped" | "cancelled";
  sentAt: string | null;
  recipients: number | null;
};

export type CampaignView = {
  id: string;
  siteId: string;
  name: string;
  audience: ContentAudience;
  status: "draft" | "scheduled" | "completed" | "cancelled";
  steps: CampaignStepView[];
  createdBy: ContentPerson;
  createdAt: string;
  updatedAt: string;
  version: number;
};

/** What people did is null below the minimum group size. */
export type CampaignCounts = { audience: number; delivered: number; viewed: number | null; clicked: number | null; acknowledged: number | null };
export type CampaignBreakdownRow = { key: string | null; names: Record<string, string> | null; audience: number; viewed: number; acknowledged: number };
export type CampaignDashboard = {
  campaignId: string;
  minimumGroupSize: number;
  total: CampaignCounts;
  steps: { stepId: string; position: number; status: string; counts: CampaignCounts }[];
  byUnit: CampaignBreakdownRow[];
  byLocation: CampaignBreakdownRow[];
};

export const campaignHref = (id: string) => `/content/campaigns/${encodeURIComponent(id)}`;
