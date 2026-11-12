import type { BodyDocument } from "@/lib/content/types";

/** Communities and posts (spec 023 US1): the shapes the API returns. Who may read, post or manage is worked out there. */
export type CommunityKind = "open" | "approval" | "closed";

export type ChannelView = { id: string; key: string; names: Record<string, string> };

export type CommunityRights = {
  member: boolean;
  pending: boolean;
  canJoin: boolean;
  canLeave: boolean;
  canPost: boolean;
  canModerate: boolean;
  canManage: boolean;
  mutedUntil: string | null;
};

export type CommunityView = {
  id: string;
  names: Record<string, string>;
  descriptions: Record<string, string>;
  kind: CommunityKind;
  orgUnitId: string | null;
  status: "active" | "archived";
  memberCount: number;
  channels: ChannelView[];
  rights: CommunityRights;
  version: number;
};

export type DirectoryRights = { canCreate: boolean; canModerate: boolean; canManageKeywords: boolean };

export type MemberRole = "member" | "moderator" | "owner";

export type MemberView = { userId: string; displayName: string; role: MemberRole; status: "active" | "pending"; source: "community" | "org_unit" };

/** Where a post or comment stands (spec 023 US8): shown, waiting for a moderator, or removed by one. */
export type ContentStatus = "published" | "visible" | "held" | "hidden" | "removed";
export type HeldReason = "keyword" | "reports";

export const reportReasons = ["spam", "harassment", "inappropriate", "misinformation", "other"] as const;
export type ReportReason = (typeof reportReasons)[number];

export type ModerationItem = {
  type: "post" | "comment";
  id: string;
  postId: string;
  communityId: string;
  communityNames: Record<string, string>;
  author: EngagementPerson;
  text: string;
  body: BodyDocument | null;
  status: ContentStatus;
  heldReason: HeldReason | null;
  reports: { reason: ReportReason; count: number }[];
  notes: string[];
  createdAt: string;
};

export type MuteView = { userId: string; displayName: string; until: string; reason: string | null };

export type KeywordFilterView = { keywords: string[]; version: number };

export type EngagementPerson = { id: string; displayName: string; active: boolean };

export const reactionKinds = ["like", "celebrate", "support", "insightful", "funny"] as const;
export type ReactionKind = (typeof reactionKinds)[number];

export type ReactionCounts = { counts: Record<ReactionKind, number>; mine: ReactionKind | null };

export type PostView = {
  id: string;
  communityId: string;
  channelId: string;
  author: EngagementPerson;
  body: BodyDocument;
  hashtags: string[];
  mentions: EngagementPerson[];
  pinned: boolean;
  publishedAt: string;
  editedAt: string | null;
  commentCount: number;
  reactions: ReactionCounts;
  following: boolean;
  mine: boolean;
  canEdit: boolean;
  canPin: boolean;
  canComment: boolean;
  status: ContentStatus;
  heldReason: HeldReason | null;
  removedReason: string | null;
  canReport: boolean;
};

export type FeedPage = { items: PostView[]; nextCursor: string | null };

export type PostSaved = { post: PostView; notReachable: EngagementPerson[] };

export type CommentView = {
  id: string;
  postId: string;
  parentId: string | null;
  author: EngagementPerson;
  body: string;
  createdAt: string;
  editedAt: string | null;
  reactions: ReactionCounts;
  mine: boolean;
  canRemove: boolean;
  status: ContentStatus;
  heldReason: HeldReason | null;
  removedReason: string | null;
  canReport: boolean;
};

export type CommentSaved = { comment: CommentView; notReachable: EngagementPerson[] };

export type HashtagPage = { tag: string; items: PostView[] };

export const engagementApi = "/api/v1/engagement";

/** The name in the viewer's language, then English, then any name it has. */
export function localName(names: Record<string, string> | undefined, locale: string, fallback = ""): string {
  return names?.[locale] ?? names?.en ?? Object.values(names ?? {})[0] ?? fallback;
}

export const communityHref = (id: string) => `/communities/${encodeURIComponent(id)}`;
export const postHref = (communityId: string, postId: string) => `/communities/${encodeURIComponent(communityId)}/posts/${encodeURIComponent(postId)}`;
export const hashtagHref = (tag: string) => `/communities/hashtags/${encodeURIComponent(tag)}`;

/** A guid as the API writes it; pages refuse anything else before calling the API. */
export const isGuid = (value: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);

export async function send(url: string, method: "POST" | "PUT" | "DELETE", body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}
