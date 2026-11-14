import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getPreferences, getShell } from "@/lib/shell/client";
import type { Crumb, NavLink, ShellLayout, ShellRegion } from "@/lib/shell/types";
import { NotificationBell } from "@/features/notifications/NotificationBell";
import { TaskBadge } from "@/features/services/TaskBadge";
import { CriticalNotices } from "@/features/content/CriticalNotices";
import { AccountMenu } from "./AccountMenu";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { GlobalMenu } from "./GlobalMenu";
import { MobileNav } from "./MobileNav";
import { PushOffer } from "@/features/app/PushOffer";
import { OfflineSync } from "@/features/app/OfflineSync";
import { KioskSession } from "@/features/kiosk/KioskSession";
import { QuickNav } from "./QuickNav";
import { SettingsPanel } from "./SettingsPanel";
import { Icon } from "@/components/ui/Icon";
import { Badge } from "@/components/ui/Badge";
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
  // The counts are read with the page, so the bell and task badge show them on first paint on every page and the bar
  // never shifts when they arrive (spec 036 DLS-092). The client components then keep them fresh.
  const [unread, tasks] = await Promise.all([
    regions.includes("notifications") ? apiGet<{ unread: number }>("/api/v1/me/notifications/count") : null,
    regions.includes("task_badge") ? apiGet<{ open: number }>("/api/v1/tasks/count") : null,
  ]);
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
        return <NotificationBell key={region} initialUnread={unread?.ok ? unread.data.unread : null} />;
      case "task_badge":
        return <TaskBadge key={region} initialOpen={tasks?.ok ? tasks.data.open : null} />;
      case "settings":
        return (
          <div key={region} className="shell-settings-group">
            {/* Page actions such as "Arrange this page" (spec 008 FR-059) sit next to settings; the page fills this place. */}
            <div className="shell-page-actions" data-shell-page-actions="" />
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
          {/* A guest is labelled as one on every page (spec 029 US4, spec 026 section 10). */}
          {shell.ok && shell.data.guest ? <Badge tone="info">{t("guest")}</Badge> : null}
        </div>
        <div className="shell-slot" data-slot="center">
          {layout.center.map(render)}
        </div>
        <div className="shell-slot" data-slot="end">
          {/* Ask TeamBeats sits in the bar, so it never floats over a card, a field or the footer (spec 008 FR-066, DLS-078). */}
          {shell.ok && shell.data.assistant ? (
            <Link className="shell-ask" href="/assistant">
              <Icon name="spark" size={18} />
              <span>{t("ask")}</span>
            </Link>
          ) : null}
          {layout.end.map(render)}
          {regions.includes("identity") ? <AccountMenu name={me.displayName} administration={shell.ok && shell.data.administration} /> : null}
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
      {shell.ok && regions.includes("global_navigation") && shell.data.mobileNav?.length ? <MobileNav items={shell.data.mobileNav} all={shell.data.globalMenu} /> : null}
      {me.sharedDevice ? (
        // A shared kiosk keeps nothing on the device: no push offer, no offline copy, and a timer that signs out (spec 030 US4).
        <KioskSession idleMinutes={me.sharedDevice.idleMinutes} />
      ) : (
        <>
          <PushOffer owner={`${me.tenant.id}:${me.id}`} />
          <OfflineSync />
        </>
      )}
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
