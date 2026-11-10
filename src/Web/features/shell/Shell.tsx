import Link from "next/link";
import { getTranslations } from "next-intl/server";
import type { Me } from "@/lib/api";
import { getPreferences, getShell } from "@/lib/shell/client";
import type { Crumb, NavLink, ShellLayout, ShellRegion } from "@/lib/shell/types";
import { NotificationBell } from "@/features/notifications/NotificationBell";
import { TaskBadge } from "@/features/services/TaskBadge";
import { CriticalNotices } from "@/features/content/CriticalNotices";
import { AccountMenu } from "./AccountMenu";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { GlobalMenu } from "./GlobalMenu";
import { LanguageSwitch } from "./LanguageSwitch";
import { QuickNav } from "./QuickNav";
import { SettingsPanel } from "./SettingsPanel";
import { Icon } from "@/components/ui/Icon";
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
  const tenantName = me.tenant.displayName || me.tenant.key;
  const regions = [...layout.start, ...layout.center, ...layout.end, ...layout.below];
  const rail = shell.ok && regions.includes("global_navigation") && shell.data.globalMenu.length > 0;

  const render = (region: ShellRegion) => {
    switch (region) {
      case "identity":
        return (
          <Link key={region} className="brand shell-brand" href="/" aria-label={t("homeOf", { name: tenantName })}>
            <svg className="shell-mark" viewBox="0 0 24 24" width="32" height="32" aria-hidden="true" focusable="false">
              <rect width="24" height="24" rx="7" />
              <path d="M12 4.5c.45 3.6 1.65 4.8 5.25 5.25-3.6.45-4.8 1.65-5.25 5.25-.45-3.6-1.65-4.8-5.25-5.25 3.6-.45 4.8-1.65 5.25-5.25z" />
            </svg>
            <bdi className="shell-tenant">{tenantName}</bdi>
          </Link>
        );
      case "global_navigation":
        // Drawn as the side rail below the bar, not inside a slot.
        return null;
      case "search":
        return <QuickNav key={region} />;
      case "notifications":
        return <NotificationBell key={region} />;
      case "task_badge":
        return <TaskBadge key={region} />;
      case "settings":
        return (
          <div key={region} className="shell-settings-group">
            <LanguageSwitch locales={locales} locale={me.locale} />
            <SettingsPanel locales={locales} locale={me.locale} values={values} />
          </div>
        );
      case "organization_indicator":
        return shell.ok && shell.data.canSwitchOrganization ? (
          <p key={region} className="shell-organization">
            {t("organization", { name: shell.data.organizations.find((o) => o.primary)?.name ?? shell.data.organizations[0]!.name })}
          </p>
        ) : null;
      case "breadcrumbs":
        return breadcrumbs.length > 0 ? <Breadcrumb key={region} className="shell-breadcrumbs" label={t("breadcrumbs")} crumbs={breadcrumbs} /> : null;
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
          {regions.includes("identity") ? <AccountMenu name={me.displayName} /> : null}
        </div>
      </div>
      {rail && shell.ok ? (
        <div className="shell-rail">
          <GlobalMenu id="global-navigation" label={t("globalNav")} links={shell.data.globalMenu} icons />
        </div>
      ) : null}
      {orgMenus.map((menu) => (
        <GlobalMenu key={menu.orgUnitId} id={`organization-${menu.orgUnitId}`} label={t("organizationNav", { name: menu.name })} links={menu.items} />
      ))}
      <div className="shell-below">{layout.below.map(render)}</div>
      <CriticalNotices />
      {shell.ok && shell.data.assistant ? (
        <Link className="shell-ask" href="/assistant">
          <Icon name="spark" size={18} />
          <span>{t("ask")}</span>
        </Link>
      ) : null}
    </header>
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
