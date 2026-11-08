import Link from "next/link";
import { getTranslations } from "next-intl/server";
import type { Me } from "@/lib/api";
import { getPreferences, getShell } from "@/lib/shell/client";
import type { Crumb, NavLink, ShellLayout, ShellRegion } from "@/lib/shell/types";
import { SignOutButton } from "@/components/shell/SignOutButton";
import { NotificationBell } from "@/features/notifications/NotificationBell";
import { TaskBadge } from "@/features/services/TaskBadge";
import { GlobalMenu } from "./GlobalMenu";
import { QuickNav } from "./QuickNav";
import { SettingsPanel } from "./SettingsPanel";
import "./shell.css";

/** The layout used when the shell API is unavailable, so the page always keeps identity, navigation and settings. */
const fallbackLayout: ShellLayout = {
  start: ["identity", "global_navigation"],
  center: ["search"],
  end: ["notifications", "task_badge", "settings"],
  below: ["organization_indicator", "breadcrumbs"],
  utilities: [],
};

/**
 * The global shell around every page (spec 008 US1, FR-001): regions in the order the published shell definition
 * gives, independent of page content. Contextual actions and utilities belong to their modules and render nothing until
 * those modules register them; notifications come from spec 015 and the task badge from spec 016.
 */
export async function Shell({ me, breadcrumbs = [] }: { me: Me; breadcrumbs?: Crumb[] }) {
  const [shell, values, t] = await Promise.all([getShell(), getPreferences(), getTranslations("shell")]);
  const layout = shell.ok ? shell.data.layout : fallbackLayout;
  const locales = me.tenant.enabledLocales.filter((l) => l === "ar" || l === "en");
  const orgMenus = shell.ok ? shell.data.organizationMenus.filter((m) => m.items.length > 0) : [];

  const render = (region: ShellRegion) => {
    switch (region) {
      case "identity":
        return (
          <div key={region} className="shell-identity">
            <Link className="brand" href="/">
              {t("home")}
            </Link>
            <bdi className="shell-user">{me.displayName}</bdi>
            <SignOutButton label={t("signOut")} />
          </div>
        );
      case "global_navigation":
        return shell.ok ? <GlobalMenu key={region} id="global-navigation" label={t("globalNav")} links={shell.data.globalMenu} /> : null;
      case "search":
        return <QuickNav key={region} />;
      case "notifications":
        return <NotificationBell key={region} />;
      case "task_badge":
        return <TaskBadge key={region} />;
      case "settings":
        return <SettingsPanel key={region} locales={locales} locale={me.locale} values={values} />;
      case "organization_indicator":
        return shell.ok && shell.data.canSwitchOrganization ? (
          <p key={region} className="shell-organization">
            {t("organization", { name: shell.data.organizations.find((o) => o.primary)?.name ?? shell.data.organizations[0]!.name })}
          </p>
        ) : null;
      case "breadcrumbs":
        return breadcrumbs.length > 0 ? <Breadcrumbs key={region} label={t("breadcrumbs")} crumbs={breadcrumbs} /> : null;
      default:
        return null;
    }
  };

  return (
    <header className="shell" data-shell-version={shell.ok ? shell.data.version : undefined}>
      <div className="shell-bar">
        <div className="shell-slot" data-slot="start">
          {layout.start.map(render)}
        </div>
        <div className="shell-slot" data-slot="center">
          {layout.center.map(render)}
        </div>
        <div className="shell-slot" data-slot="end">
          {layout.end.map(render)}
        </div>
      </div>
      {orgMenus.map((menu) => (
        <GlobalMenu key={menu.orgUnitId} id={`organization-${menu.orgUnitId}`} label={t("organizationNav", { name: menu.name })} links={menu.items} />
      ))}
      <div className="shell-below">{layout.below.map(render)}</div>
    </header>
  );
}

export function Breadcrumbs({ label, crumbs }: { label: string; crumbs: Crumb[] }) {
  return (
    <nav aria-label={label} className="shell-breadcrumbs">
      <ol>
        {crumbs.map((c, i) => (
          <li key={c.href}>
            {i === crumbs.length - 1 ? (
              <a href={c.href} aria-current="page">
                <bdi>{c.label}</bdi>
              </a>
            ) : (
              <a href={c.href}>
                <bdi>{c.label}</bdi>
              </a>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

/** The section menu of the nearest IA ancestor that has one (spec 008 FR-011). */
export function SectionMenu({ label, links }: { label: string; links: NavLink[] }) {
  if (links.length === 0) {
    return null;
  }
  return (
    <nav aria-label={label} className="shell-section">
      <ul>
        {links.map((l) => (
          <li key={l.nodeId}>
            <a href={l.href}>
              <bdi>{l.label}</bdi>
            </a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
