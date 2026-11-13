import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AuditPage, IdentityProvider, OrgUnitView, UserSummary } from "@/lib/admin/types";
import type { Microsoft365Connection, PackConnection } from "@/lib/integrations/types";
import { groupSections } from "@/components/shell/AdminNav";
import { Badge } from "@/components/ui/Badge";
import { Icon } from "@/components/ui/Icon";
import { PageHeader } from "@/components/ui/PageHeader";
import { relativeTime, renderTime } from "@/lib/format";
import "@/features/admin/admin.css";

type Step = { key: "identity" | "organization" | "users" | "connections"; href: string; done: boolean };

/**
 * The admin overview (spec 006 FR-018): setup status, the latest audited changes and the sections by group. Every block
 * reads an API the viewer may already call for that section, so a block shows only when its section is in the
 * viewer's navigation, and the API still checks each read.
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

  return (
    <>
      <PageHeader title={t("title")} description={t("landing.intro")} icon="settings" />
      <div className="admin-overview">
        {steps.length > 0 ? (
          <section className="ui-card admin-overview-card" aria-labelledby="admin-setup">
            <h2 id="admin-setup">{t("landing.setup")}</h2>
            <p className="field-hint">{t("landing.setupIntro")}</p>
            <ul className="admin-overview-list">
              {steps.map((s) => (
                <li key={s.key} className="admin-overview-row">
                  <Icon name={s.done ? "check" : "clock"} size={18} />
                  <Link href={s.href}>{t(`landing.steps.${s.key}`)}</Link>
                  <Badge tone={s.done ? "success" : "warning"}>{s.done ? t("landing.done") : t("landing.todo")}</Badge>
                </li>
              ))}
            </ul>
          </section>
        ) : null}
        {audit?.ok ? (
          <section className="ui-card admin-overview-card" aria-labelledby="admin-recent">
            <h2 id="admin-recent">{t("landing.recent")}</h2>
            <p className="field-hint">{t("landing.recentIntro")}</p>
            {audit.data.items.length === 0 ? (
              <p>{t("landing.noRecent")}</p>
            ) : (
              <ul className="admin-overview-list">
                {audit.data.items.map((e) => (
                  <li key={e.id} className="admin-overview-row">
                    <code dir="ltr">{e.action}</code>
                    <time dateTime={e.occurredAt}>{relativeTime(e.occurredAt, locale, now)}</time>
                  </li>
                ))}
              </ul>
            )}
            <Link href="/admin/audit">{t("landing.viewAudit")}</Link>
          </section>
        ) : null}
      </div>
      <section aria-labelledby="admin-sections" className="admin-overview-sections">
        <h2 id="admin-sections">{t("landing.sections")}</h2>
        <div className="admin-overview-groups">
          {groups.map(({ group, sections }) => (
            <section key={group} className="ui-card admin-overview-card" aria-labelledby={`admin-group-${group}`}>
              <h3 id={`admin-group-${group}`}>{t(`navGroups.${group}`)}</h3>
              <ul className="admin-overview-links">
                {sections.map((s) => (
                  <li key={s.key}>
                    <Link href={s.path}>{s.label}</Link>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      </section>
    </>
  );
}
