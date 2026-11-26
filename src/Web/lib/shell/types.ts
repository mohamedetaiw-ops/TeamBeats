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

/** An organization's menu; `shared` when another organization shared it with the viewer's, so the owner shows (spec 033 FR-022). */
export type OrganizationMenu = { orgUnitId: string; name: string; items: NavLink[]; shared?: boolean };

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
  /** True when the viewer may open at least one administration section, so the account menu offers Administration (FR-062). */
  administration: boolean;
  /** Bottom navigation on phones (spec 030 FR-005): at most five global menu entries this viewer may open. */
  mobileNav?: NavLink[] | null;
  /** True for a guest of the tenant: the API sends the reduced guest shell and the home lists their spaces (spec 026 section 10). */
  guest?: boolean;
  /** The viewer's brand title, logo and lockup in their language (spec 033 R2 T018); absent values fall back to the tenant. */
  brand?: { title: string | null; logoUrl: string | null; lockupUrl: string | null } | null;
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

/** A quick navigation match; `ranked` marks one moved up by the person's own use (spec 008 R3). */
export type QuickNavItem = { label: string; href: string; owner: string | null; ranked?: boolean };

/** Behavioral personalization (spec 008 R3, FR-033): the tenant policy and the person's own choice. */
export type BehaviorPolicy = { available: boolean; enabled: boolean; consentMode: "opt_in" | "opt_out"; updatedAt: string | null };
export type BehaviorChoice = { available: boolean; consentMode: "opt_in" | "opt_out"; consented: boolean | null; active: boolean };

export type PreferenceKey = "mode" | "density" | "motion" | "text_scale" | "focus";

export type PreferenceSetting = { key: PreferenceKey; values: string[]; default: string; scope: string };

/** `chosen` names the settings the person saved; the others are defaults a design may replace. */
export type PreferenceProfile = { schemaVersion: number; values: Record<PreferenceKey, string>; schema: PreferenceSetting[]; chosen?: string[] };

export const defaultPreferences: Record<PreferenceKey, string> = { mode: "light", density: "comfortable", motion: "system", text_scale: "100", focus: "off" };

/** A favorite as its owner sees it now (spec 008 FR-017): `href` is null when it can't be opened, and `reason` says why. */
export type Favorite = { id: string; kind: "page" | "item"; label: string; href: string | null; available: boolean; reason: string | null; createdAt: string };

/** A recent visit: the path alone, never a query string or fragment. */
export type RecentVisit = { path: string; label: string; visitedAt: string };

/** A command the person may use now (spec 008 FR-008): `key` names its label, `href` opens the module's page. */
export type CommandItem = { key: string; href: string; icon: string };

/** A help hint or short tour for the current page, in the viewer's language (spec 008 FR-020). */
export type HelpItem = { key: string; kind: "hint" | "tour"; path: string; title: string; body: string | null; steps: { title: string; body: string }[]; dismissed: boolean };

/** The theme mode a mode preference asks for (spec 008, spec 033 R2 T017): system follows the device, including its contrast setting. */
export function themeMode(mode: string | undefined): "system" | "light" | "dark" | "high_contrast" {
  return mode === "light" || mode === "dark" || mode === "high_contrast" ? mode : "system";
}
