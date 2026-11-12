import type { IconName } from "@/components/ui/Icon";
import { PACK_KINDS, type Microsoft365Connection, type PackConnection } from "./types";

/**
 * How each connection type looks in the Connections area (spec 006 FR-016, spec 036 DLS-076): its group in the "Add
 * connection" wizard and its product mark, or an icon from the icon set when the type has no published mark. This is
 * presentation only: whether a type is in the tenant's package, and every rule about it, comes from the API.
 */
export const CONNECTION_TYPES = ["microsoft365", ...PACK_KINDS] as const;
export type ConnectionType = (typeof CONNECTION_TYPES)[number];

export const TYPE_GROUPS = ["workplace", "service_desk", "people_time", "learning", "finance", "custom"] as const;
export type TypeGroup = (typeof TYPE_GROUPS)[number];

type Look = { group: TypeGroup; mark?: string; icon: IconName };

const LOOKS: Record<ConnectionType, Look> = {
  microsoft365: { group: "workplace", mark: "/connectors/microsoft.svg", icon: "building" },
  google_workspace: { group: "workplace", mark: "/connectors/google.svg", icon: "globe" },
  slack: { group: "workplace", mark: "/connectors/slack.svg", icon: "chat" },
  onprem_ad: { group: "workplace", mark: "/connectors/microsoft.svg", icon: "building" },
  servicenow: { group: "service_desk", icon: "inbox" },
  jira_service_management: { group: "service_desk", mark: "/connectors/atlassian.svg", icon: "inbox" },
  hr_rest: { group: "people_time", icon: "people" },
  hr_sftp: { group: "people_time", icon: "folder" },
  clock_import: { group: "people_time", icon: "clock" },
  lms_rest: { group: "learning", icon: "book" },
  finance_rest: { group: "finance", icon: "files" },
  custom_rest: { group: "custom", icon: "globe" },
  webhook: { group: "custom", icon: "link" },
};

export function isConnectionType(value: string | null | undefined): value is ConnectionType {
  return value != null && (CONNECTION_TYPES as readonly string[]).includes(value);
}

export function typeLook(kind: string): Look {
  return isConnectionType(kind) ? LOOKS[kind] : { group: "custom", icon: "globe" };
}

/** The wizard's steps, in order (spec 006 FR-016). */
export const WIZARD_STEPS = ["choose", "setup", "test", "enable"] as const;
export type WizardStep = (typeof WIZARD_STEPS)[number];

export const NEW_CONNECTION = "/admin/connections/new";

export function wizardHref(type?: string, id?: string, step?: "test" | "enable"): string {
  const query = new URLSearchParams();
  if (type) query.set("type", type);
  if (id) query.set("id", id);
  if (step) query.set("step", step);
  const text = query.toString();
  return text ? `${NEW_CONNECTION}?${text}` : NEW_CONNECTION;
}

export function manageHref(kind: string, id: string): string {
  return kind === "microsoft365" ? `/admin/connections/microsoft365/${id}` : `/admin/connections/${id}`;
}

/** One established connection as the overview shows it; Microsoft 365 and connector packs read the same. */
export type OverviewRow = {
  id: string;
  kind: string;
  name: string;
  status: string;
  enabled: boolean;
  lastSuccessAt: string | null;
  /** Features turned on (Microsoft 365) or actions (connector packs). */
  count: number;
};

export function overviewRows(microsoft: Microsoft365Connection[], packs: PackConnection[]): OverviewRow[] {
  return [
    ...microsoft.map((c) => ({
      id: c.id,
      kind: "microsoft365",
      name: c.displayName,
      status: c.status,
      enabled: c.enabled,
      lastSuccessAt: c.lastSuccessAt,
      count: c.features.filter((f) => f.enabled).length,
    })),
    ...packs.map((c) => ({ id: c.id, kind: c.kind, name: c.displayName, status: c.status, enabled: c.enabled, lastSuccessAt: c.lastSuccessAt, count: c.actions.length })),
  ];
}
