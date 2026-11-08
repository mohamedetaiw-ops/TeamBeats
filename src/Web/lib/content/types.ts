/** Spec 009 content: shapes returned by `/api/v1/content/*`. The body is a block document, never HTML. */
export type Inline = { text: string; marks?: string[] | null; link?: string | null };

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
};

export type CommentPage = { items: CommentView[]; commentsOn: boolean; moderator: boolean };

export const reactionKinds = ["like", "celebrate", "support", "insightful"] as const;
export type ReactionKind = (typeof reactionKinds)[number];

export type ReactionSummary = { itemId: string; counts: Record<ReactionKind, number>; mine: ReactionKind | null };

/** Where a reader opens an item. */
export const readHref = (id: string) => `/content/read/${encodeURIComponent(id)}`;
