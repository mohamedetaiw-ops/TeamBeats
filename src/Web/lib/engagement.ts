import type { BodyDocument } from "@/lib/content/types";

/** Communities and posts (spec 023 US1): the shapes the API returns. Who may read, post or manage is worked out there. */
export type CommunityKind = "open" | "approval" | "closed";

/** A discussion channel, or a Speak Up channel whose posts a moderator reviews first and which may allow anonymous posts (spec 023 US2). */
export type ChannelView = { id: string; key: string; names: Record<string, string>; kind: "discussion" | "speak_up"; allowAnonymous: boolean };

export type CommunityRights = {
  member: boolean;
  pending: boolean;
  canJoin: boolean;
  canLeave: boolean;
  canPost: boolean;
  canModerate: boolean;
  canManage: boolean;
  mutedUntil: string | null;
  canAddSpeakUp: boolean;
  canAllowAnonymous: boolean;
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

export type DirectoryRights = { canCreate: boolean; canModerate: boolean; canManageKeywords: boolean; surveys: boolean };

export type MemberRole = "member" | "moderator" | "owner";

export type MemberView = { userId: string; displayName: string; role: MemberRole; status: "active" | "pending"; source: "community" | "org_unit" };

/** Where a post or comment stands (spec 023 US8): shown, waiting for a moderator, or removed by one. */
export type ContentStatus = "published" | "visible" | "held" | "hidden" | "removed";
export type HeldReason = "keyword" | "reports" | "speak_up";

export const reportReasons = ["spam", "harassment", "inappropriate", "misinformation", "other"] as const;
export type ReportReason = (typeof reportReasons)[number];

export type ModerationItem = {
  type: "post" | "comment" | "anonymous";
  id: string;
  postId: string;
  communityId: string;
  communityNames: Record<string, string>;
  author: EngagementPerson | null;
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
  anonymous: boolean;
  editedByModerator: boolean;
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

/** What the anonymous author keeps: the token for the one-time link where they read the moderator's decision. */
export type AnonymousReceipt = { replyToken: string };

export type AnonymousReply = { status: "pending" | "published" | "answered" | "declined"; answer: string | null; reason: string | null; communityId: string; postId: string | null };

export const speakUpReplyPath = "/communities/speak-up/reply";

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

/** A poll in a community (spec 023 US3). Counts are null until results show for this viewer; voters only for the author and moderators of a named poll. */
export type PollOptionView = { id: string; label: string; count: number | null; voters: EngagementPerson[] | null };

export type PollView = {
  id: string;
  communityId: string;
  author: EngagementPerson;
  question: string;
  multiple: boolean;
  anonymous: boolean;
  allowChange: boolean;
  results: "after_vote" | "after_close";
  closesAt: string;
  closed: boolean;
  voted: boolean;
  myChoices: string[];
  resultsVisible: boolean;
  voters: number | null;
  options: PollOptionView[];
  canVote: boolean;
  canClose: boolean;
  canAddOptions: boolean;
  createdAt: string;
};

export type SurveyKind = "single" | "multiple" | "scale" | "text";
export type SurveyStatus = "draft" | "open" | "closed";
export type SurveyOptionView = { id: string; label: string };
export type SurveyQuestionView = { id: string; kind: SurveyKind; prompt: string; required: boolean; options: SurveyOptionView[] };

export type SurveyView = {
  id: string;
  orgUnitId: string;
  unitNames: Record<string, string>;
  author: EngagementPerson;
  title: string;
  intro: string | null;
  anonymous: boolean;
  threshold: number;
  status: SurveyStatus;
  closesAt: string | null;
  publishedAt: string | null;
  closedAt: string | null;
  createdAt: string;
  questions: SurveyQuestionView[];
  responded: boolean;
  canRespond: boolean;
  manages: boolean;
  canEdit: boolean;
};

export type SurveyList = { canCreate: boolean; defaultThreshold: number; toAnswer: SurveyView[]; managed: SurveyView[] };

export type SurveyQuestionResult = {
  id: string;
  kind: SurveyKind;
  prompt: string;
  answered: number;
  options: { id: string; label: string; count: number }[];
  scale: number[] | null;
  average: number | null;
  texts: string[] | null;
};

export type SurveyGroupResult = { unitId: string; names: Record<string, string>; responses: number | null; visible: boolean };

export type SurveyResults = {
  surveyId: string;
  anonymous: boolean;
  threshold: number;
  status: SurveyStatus;
  group: string | null;
  responses: number;
  visible: boolean;
  groups: SurveyGroupResult[];
  questions: SurveyQuestionResult[];
};

export type SurveyNamedResponse = {
  person: EngagementPerson;
  submittedAt: string;
  answers: { questionId: string; optionIds: string[]; value: number | null; text: string | null }[];
};

export type SurveySettingsView = { threshold: number; version: number };

export const surveysHref = "/communities/surveys";
export const surveyHref = (id: string) => `/communities/surveys/${encodeURIComponent(id)}`;
