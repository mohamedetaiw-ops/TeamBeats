import { problemMessages } from "@/lib/notifications/types";

/** Project and team spaces (spec 029): the shapes the API returns. Who reads, contributes or owns is decided there. */
export const spacesApi = "/api/v1/spaces";

export const spacesHref = "/spaces";
export const newSpaceHref = "/spaces/new";
export const manageSpacesHref = "/spaces/manage";
export const spaceHref = (id: string) => `/spaces/${id}`;
export const spaceTasksHref = (id: string) => `/spaces/${id}/tasks`;
export const spaceMembersHref = (id: string) => `/spaces/${id}/members`;
export const spaceFilesHref = (id: string) => `/spaces/${id}/files`;
export const spaceDiscussionHref = (id: string) => `/spaces/${id}/discussion`;
export const spaceCalendarHref = (id: string, month?: string) => `/spaces/${id}/calendar${month ? `?month=${month}` : ""}`;
export const spaceSettingsHref = (id: string) => `/spaces/${id}/settings`;
export const taskHref = (space: string, task: string) => `/spaces/${space}/tasks/${task}`;
export const myTasksHref = "/tasks?tab=mine";

export type SpaceKind = "project" | "team";
export type Visibility = "public" | "private" | "secret";
export type SpaceRole = "owner" | "member" | "viewer";
export type Priority = "low" | "normal" | "high" | "urgent";
export type StatusValue = "on_track" | "at_risk" | "off_track" | "done";

export const visibilities: Visibility[] = ["public", "private", "secret"];
export const roles: SpaceRole[] = ["owner", "member", "viewer"];
export const priorities: Priority[] = ["low", "normal", "high", "urgent"];
export const statusValues: StatusValue[] = ["on_track", "at_risk", "off_track", "done"];

export type SpacesRights = { enabled: boolean; canCreate: boolean; canManage: boolean; guests: boolean };
export type PersonRef = { userId: string; displayName: string };
export type SpaceRights = { role: SpaceRole | null; reads: boolean; contributes: boolean; owns: boolean; manages: boolean; canJoin: boolean; canLeave: boolean };

export type SpaceView = {
  id: string;
  kind: SpaceKind;
  visibility: Visibility;
  name: string;
  description: string;
  allowJoin: boolean;
  classification: "internal" | "confidential";
  startsOn: string | null;
  endsOn: string | null;
  status: "active" | "archived";
  memberCount: number;
  owners: PersonRef[];
  rights: SpaceRights;
  createdAt: string;
  updatedAt: string;
  version: number;
  /** The space's library (spec 011) and discussion (spec 023), for its readers; null when not in the plan. */
  libraryId: string | null;
  communityId: string | null;
};

/** A space the viewer is a guest of (spec 026 section 10), as GET /api/v1/spaces/as-guest answers. */
export type GuestSpace = { spaceId: string; name: string; role: string; sponsorName: string; expiresAt: string | null };

export type SpaceSummary = {
  id: string;
  kind: SpaceKind;
  visibility: Visibility;
  name: string;
  description: string;
  status: "active" | "archived";
  memberCount: number;
  role: SpaceRole | null;
  canOpen: boolean;
  updatedAt: string;
};

export type SpaceMember = { userId: string; displayName: string; role: SpaceRole; guest: boolean; addedAt: string; expiresAt?: string | null };

export type SpaceGuest = { userId: string; displayName: string; role: SpaceRole; expiresAt: string | null };
export type GuestInvitation = { id: string; email: string; role: SpaceRole; expiresAt: string; emailState: "sent" | "retry" | "bounced" | "failed"; invitedAt: string };
/** The guests of a space for its owners (spec 029 US4). The server decides whether guests are allowed and the most days an owner can give. */
export type SpaceGuests = {
  allowed: boolean;
  canInvite: boolean;
  maxDays: number;
  limit: number;
  confidential: boolean;
  confidentialAllowed: boolean;
  guests: SpaceGuest[];
  invitations: GuestInvitation[];
};
export type TemplateOption = { key: string; name: string; kind: SpaceKind; builtIn: boolean };

export type ColumnView = { id: string; name: string; done: boolean; position: number };
export type ListView = { id: string; name: string; position: number; columns: ColumnView[] };

export type TaskCard = {
  id: string;
  listId: string;
  columnId: string;
  title: string;
  priority: Priority;
  labels: string[];
  dueOn: string | null;
  status: "open" | "done";
  overdue: boolean;
  assignees: PersonRef[];
  milestoneId: string | null;
  checklist: { done: number; total: number };
  comments: number;
  position: number;
  version: number;
};

export type TaskBoard = { lists: ListView[]; tasks: TaskCard[]; canEdit: boolean };

export type TaskDetail = {
  task: TaskCard;
  description: string;
  createdBy: PersonRef;
  createdAt: string;
  completedAt: string | null;
  checklist: { id: string; text: string; done: boolean }[];
  comments: { id: string; author: PersonRef; body: string; createdAt: string }[];
  canEdit: boolean;
  /** Linked library files the viewer may open, at the version readers get now. */
  files: TaskFile[];
};

export type TaskFile = { documentId: string; name: string; version: string; fileName: string; contentType: string; bytes: number };

export type MyTask = { task: TaskCard; spaceId: string; spaceName: string };

export type SpaceEvent = { id: string; title: string; description: string; startsAt: string; endsAt: string; allDay: boolean; createdBy: PersonRef; version: number };

/** The team calendar (US6): never the leave type. `end` of an absence is exclusive. */
export type SpaceCalendarData = {
  from: string;
  to: string;
  timeZone: string;
  events: SpaceEvent[];
  milestones: { id: string; name: string; dueOn: string; reached: boolean }[];
  tasks: { id: string; title: string; dueOn: string; status: "open" | "done"; assignees: PersonRef[] }[];
  away: { person: PersonRef; start: string; end: string; startHalf: boolean; endHalf: boolean }[];
  canEdit: boolean;
};

export type SpaceAvailability = {
  state: string;
  date: string;
  members: { person: PersonRef; shared: boolean; blocks: { status: string; start: string; end: string }[] }[];
};

export type MilestoneView = { id: string; name: string; dueOn: string | null; done: number; total: number; reached: boolean; overdue: boolean; version: number };
export type StatusView = { id: string; status: StatusValue; note: string; setBy: PersonRef; setAt: string };

export type SpaceOverview = {
  status: StatusView | null;
  history: StatusView[];
  milestones: MilestoneView[];
  openTasks: number;
  doneTasks: number;
  overdueTasks: number;
  overdue: { id: string; title: string; dueOn: string }[];
  canSetStatus: boolean;
  canEdit: boolean;
};

/** The request body of a task change: everything the task holds that a member may set. */
export function taskBody(task: TaskCard, description: string) {
  return {
    title: task.title,
    description,
    priority: task.priority,
    labels: task.labels,
    dueOn: task.dueOn,
    assignees: task.assignees.map((a) => a.userId),
    milestoneId: task.milestoneId,
    version: task.version,
  };
}

export async function send(url: string, method: "POST" | "PUT" | "DELETE", body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/** A call's result, or the API's messages (already in the viewer's language) when it was refused. */
export async function call<T>(request: () => Promise<Response>, failed: string): Promise<{ ok: true; value: T } | { ok: false; messages: string[] }> {
  try {
    const response = await request();
    if (response.ok) return { ok: true, value: (response.status === 204 ? undefined : await response.json()) as T };
    return { ok: false, messages: await problemMessages(response) };
  } catch {
    return { ok: false, messages: [failed] };
  }
}

export const isGuid = (value: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
