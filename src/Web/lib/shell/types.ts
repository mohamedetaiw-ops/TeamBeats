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

/** One footer link, already in the viewer's language (spec 008 FR-056). */
export type FooterLink = { kind: "privacy" | "acceptable_use" | "accessibility" | "other"; label: string; href: string };

/** The footer the tenant configured; null when it configured none, so no footer is drawn. */
export type ShellFooter = { organization: string | null; links: FooterLink[]; classification: string | null };

/** One more view of the home, as a tab after My day (spec 008 FR-058); only views this viewer may see arrive. */
export type HomeView = { id: string; label: string; slug: string };

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
  /** True when the tenant has the assistant, so the shell offers Ask TeamBeats (spec 008 FR-056). */
  assistant: boolean;
  footer?: ShellFooter | null;
  homeViews?: HomeView[] | null;
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
