import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AuditPage, IdentityProvider, OrgUnitView, UserSummary } from "@/lib/admin/types";
import type { Microsoft365Connection, PackConnection } from "@/lib/integrations/types";
import { groupSections } from "@/components/shell/AdminNav";
import { Badge } from "@/components/ui/Badge";
import { Icon, type IconName } from "@/components/ui/Icon";
import { PageHeader } from "@/components/ui/PageHeader";
import { TileCard } from "@/components/ui/TileCard";
import { Widget } from "@/components/ui/Widget";
import { relativeTime, renderTime } from "@/lib/format";
import "@/features/admin/admin.css";

type Step = { key: "identity" | "organization" | "users" | "connections"; href: string; done: boolean };

/** One icon per admin navigation group (DLS-081), shared by the shortcut tiles. */
const GROUP_ICON: Record<string, IconName> = { workspace: "settings", people: "people", hr: "user", content: "news", security: "lock", ai: "spark" };
const SHORTCUTS = 6;

/**
 * The admin overview (spec 006 FR-018): setup status, the latest audited changes in words, and shortcuts. Every block
 * reads an API the viewer may already call for that section, so a block shows only when its section is in the
 * viewer's navigation, and the API still checks each read. Blocks are shared widgets (spec 036 DLS-089); the full
 * section list is the admin menu beside the page, so the page does not repeat it.
 */
export default async function AdminHome() {
  const [t, nav, locale] = await Promise.all([getTranslations("admin"), apiGet<NavigationSection[]>("/api/v1/admin/navigation"), getLocale()]);
  const keys = new Set(nav.ok ? nav.data.map((s) => s.key) : []);
  const [identity, units, users, packs, m365, audit] = await Promise.all([
    keys.has("identity") ? apiGet<IdentityProvider | null>("/api/v1/tenant/identity-provider") : null,
    keys.has("organization") ? apiGet<OrgUnitView[]>("/api/v1/org/units") : null,
    keys.has("users") ? apiGet<UserSummary[]>("/api/v1/admin/users") : null,
    keys.has("connections") ? apiGet<PackConnection[]>("/api/v1/integrations/pack-connections") : null,
    keys.has("connections") ? apiGet<Microsoft365Connection[]>("/api/v1/integrations/connections") : null,
    keys.has("audit") ? apiGet<AuditPage>("/api/v1/audit/events?limit=6") : null,
  ]);

  const steps: Step[] = [];
  if (identity?.ok) steps.push({ key: "identity", href: "/admin/identity", done: identity.data !== null });
  if (units?.ok) steps.push({ key: "organization", href: "/admin/organization", done: units.data.length > 1 });
  if (users?.ok) steps.push({ key: "users", href: "/admin/users", done: users.data.length > 1 });
  if (packs?.ok || m365?.ok)
    steps.push({ key: "connections", href: "/admin/connections", done: (packs?.ok ? packs.data.length : 0) + (m365?.ok ? m365.data.length : 0) > 0 });
  const groups = nav.ok ? await groupSections(nav.data) : [];
  const now = renderTime();

  // Shortcuts (FR-018): sections with items waiting first, then setup steps still to do, then the first section of each
  // group, at most six. TeamBeats records no per-person section use yet, so this order stands in for "most used".
  const all = groups.flatMap(({ group, sections }) => sections.map((s) => ({ ...s, group })));
  const open = new Set(steps.filter((s) => !s.done).map((s) => s.href));
  const picked = [
    ...all.filter((s) => (s.badge ?? 0) > 0),
    ...all.filter((s) => open.has(s.path)),
    ...groups.map(({ group, sections }) => ({ ...sections[0]!, group })),
  ].filter((s, i, list) => list.findIndex((x) => x.key === s.key) === i).slice(0, SHORTCUTS);

  // An audited action in words: its own label when one exists, otherwise the area it belongs to (spec 006 FR-018).
  const actionLabel = (action: string) => {
    const own = `auditActions.${action.replaceAll(".", "_")}`;
    if (t.has(own)) return t(own);
    const prefix = action.split(".")[0] ?? "";
    return t("landing.change", { area: t(t.has(`auditAreas.${prefix}`) ? `auditAreas.${prefix}` : "auditAreas.other") });
  };

  return (
    <>
      <PageHeader title={t("title")} description={t("landing.intro")} icon="settings" />
      <div className="admin-overview">
        {steps.length > 0 ? (
          <Widget title={t("landing.setup")}>
            <p className="field-hint">{t("landing.setupIntro")}</p>
            <ul className="ui-widget-list">
              {steps.map((s) => (
                <li key={s.key} className="ui-widget-row">
                  <span className="ui-icon-tile" data-tone={s.done ? "success" : "highlight"} aria-hidden="true">
                    <Icon name={s.done ? "check" : "clock"} size={16} />
                  </span>
                  <Link href={s.href} className="ui-widget-row-main">
                    {t(`landing.steps.${s.key}`)}
                  </Link>
                  <Badge tone={s.done ? "success" : "warning"}>{s.done ? t("landing.done") : t("landing.todo")}</Badge>
                </li>
              ))}
            </ul>
          </Widget>
        ) : null}
        {audit?.ok ? (
          <Widget title={t("landing.recent")} footer={<Link href="/admin/audit">{t("landing.viewAudit")}</Link>}>
            <p className="field-hint">{t("landing.recentIntro")}</p>
            {audit.data.items.length === 0 ? (
              <p className="field-hint">{t("landing.noRecent")}</p>
            ) : (
              <ul className="ui-widget-list">
                {audit.data.items.map((e) => (
                  <li key={e.id} className="ui-widget-row">
                    <span className="ui-widget-row-main">
                      <span>{actionLabel(e.action)}</span>
                      <span className="ui-widget-row-meta" dir="ltr">
                        {e.action}
                      </span>
                    </span>
                    {e.outcome.toLowerCase() !== "success" ? <Badge tone="danger">{t("landing.failed")}</Badge> : null}
                    <time className="ui-widget-row-meta" dateTime={e.occurredAt}>
                      {relativeTime(e.occurredAt, locale, now)}
                    </time>
                  </li>
                ))}
              </ul>
            )}
          </Widget>
        ) : null}
      </div>
      {picked.length > 0 ? (
        <section aria-labelledby="admin-shortcuts" className="admin-overview-shortcuts">
          <div className="admin-overview-shortcuts-head">
            <h2 id="admin-shortcuts">{t("landing.shortcuts")}</h2>
            <p className="field-hint">{t("landing.shortcutsIntro")}</p>
          </div>
          <ul className="ui-tiles">
            {picked.map((s) => (
              <li key={s.key}>
                <TileCard
                  title={s.label}
                  href={s.path}
                  icon={GROUP_ICON[s.group] ?? "settings"}
                  meta={s.badge ? t("landing.needsYou", { count: s.badge }) : t(`navGroups.${s.group}`)}
                />
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </>
  );
}
