/**
 * Shapes of the spec 008 shell, resolve, quick navigation and preference APIs. The API decides which links exist and
 * which the viewer may open; the shell only draws what it receives (constitution XXX).
 */
export type ShellRegion =
  | "identity"
  | "global_navigation"
  | "organization_indicator"
  | "search"
  | "notifications"
  | "task_badge"
  | "contextual_actions"
  | "breadcrumbs"
  | "settings"
  | "utilities";

export type ShellLayout = {
  start: ShellRegion[];
  center: ShellRegion[];
  end: ShellRegion[];
  below: ShellRegion[];
  utilities: string[];
  homePageId?: string | null;
};

export type NavLink = { nodeId: string; label: string; href: string; owner: string | null; children: NavLink[] };

export type OrganizationMenu = { orgUnitId: string; name: string; items: NavLink[] };

export type OrganizationIndicator = { id: string; name: string; primary: boolean };

export type Shell = {
  version: number;
  layout: ShellLayout;
  globalMenu: NavLink[];
  organizationMenus: OrganizationMenu[];
  organizations: OrganizationIndicator[];
  canSwitchOrganization: boolean;
  /** Slug of the employee home when it is published and the viewer may open it. */
  home: string | null;
};

export type Crumb = { label: string; href: string };

export type Resolved = {
  kind: "page" | "redirect" | "recovery";
  pageSlug: string | null;
  redirectTo: string | null;
  nodeId: string | null;
  breadcrumbs: Crumb[];
  sectionMenu: NavLink[];
  recovery: Crumb[];
};

export type QuickNavItem = { label: string; href: string; owner: string | null };

export type PreferenceKey = "mode" | "density" | "motion" | "text_scale";

export type PreferenceSetting = { key: PreferenceKey; values: string[]; default: string; scope: string };

export type PreferenceProfile = { schemaVersion: number; values: Record<PreferenceKey, string>; schema: PreferenceSetting[] };

export const defaultPreferences: Record<PreferenceKey, string> = { mode: "light", density: "comfortable", motion: "system", text_scale: "100" };
