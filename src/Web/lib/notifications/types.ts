/** Shapes of the spec 015 notification API, as the browser reads them. */
export type NotificationItem = {
  id: string;
  category: string;
  mandatory: boolean;
  urgent: boolean;
  title: string;
  body: string;
  link: string;
  count: number;
  createdAt: string;
  updatedAt: string;
  readAt: string | null;
};

export type NotificationPage = { items: NotificationItem[]; nextCursor: string | null; unread: number };

export type NotificationFilter = "all" | "unread" | "mandatory";

export type CategoryPreference = { category: string; mandatory: boolean; email: boolean; digest: boolean; slack: boolean };

export type Preferences = {
  categories: CategoryPreference[];
  quietHours: { start: string; end: string } | null;
  digestTime: string;
  timeZone: string;
  emailPaused: boolean;
  /** Slack direct messages (spec 015 US8): available when the organization connected Slack; linked once the person linked their account. */
  slack: { available: boolean; linked: boolean };
};

export type RelaySettings = { host: string; port: number; username: string | null; secretReference: string | null; startTls: boolean };

export type NotificationSettings = {
  senderName: string | null;
  relay: RelaySettings | null;
  defaultEmail: Record<string, boolean>;
  extraMandatory: string[];
  platformMandatory: string[];
  tenantRelayInPlan: boolean;
  version: number;
};

/** Problem Details validation errors as the API returns them: resource keys with localized messages. */
export type Problem = { code?: string; errors?: Record<string, { key: string; message: string }[]> };

export async function problemMessages(response: Response): Promise<string[]> {
  try {
    const problem = (await response.json()) as Problem & { title?: string; detail?: string };
    const messages = Object.values(problem.errors ?? {}).flatMap((list) => list.map((e) => e.message));
    return messages.length > 0 ? messages : [problem.detail ?? problem.title ?? ""].filter(Boolean);
  } catch {
    return [];
  }
}
