import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { editHref, pickName, type ContentSettingsView, type HealthItem, type SiteHealth } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { Badge } from "@/components/ui/Badge";
import { StateView } from "@/components/ui/StateView";
import { RetentionSettings } from "@/features/content/RetentionSettings";
import "@/features/content/content.css";

const kinds = ["overdue", "ownerless", "expired"] as const;

/**
 * Content health for communications administrators (spec 009 US3 scenario 4): per site, items overdue for review,
 * without an active owner and expired, with the items of one kind listed on request; sites with no owner and archived
 * sites whose items are still live; and retention per content type (FR-020).
 */
export default async function ContentHealthPage({ searchParams }: { searchParams: Promise<Record<string, string | undefined>> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const query = await searchParams;
  const [health, settings] = await Promise.all([apiGet<SiteHealth[]>("/api/v1/content/health"), apiGet<ContentSettingsView>("/api/v1/content/settings")]);
  const t = await getTranslations("content.health");
  const locale = await getLocale();
  const kind = kinds.find((k) => k === query.kind);
  const list = health.ok && query.site && kind ? await apiGet<HealthItem[]>(`/api/v1/content/health/items?site=${encodeURIComponent(query.site)}&kind=${kind}`) : null;
  const number = new Intl.NumberFormat(locale);
  const count = (s: SiteHealth, k: (typeof kinds)[number]) => (k === "overdue" ? s.overdueForReview : k === "ownerless" ? s.withoutActiveOwner : s.expired);
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/content/health" }]} />
      <main id="main" className="page content-manage">
        <h1>{t("title")}</h1>
        {!health.ok ? (
          <StateView kind="denied" title={t("denied")} message="" />
        ) : (
          <>
            <table className="content-table">
              <caption className="visually-hidden">{t("title")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("site")}</th>
                  {kinds.map((k) => (
                    <th key={k} scope="col">
                      {t(`kind.${k}`)}
                    </th>
                  ))}
                  <th scope="col">{t("siteState")}</th>
                </tr>
              </thead>
              <tbody>
                {health.data.map((s) => (
                  <tr key={s.siteId}>
                    <th scope="row">
                      <bdi>{pickName(s.names, locale)}</bdi>
                    </th>
                    {kinds.map((k) => (
                      <td key={k}>
                        {count(s, k) > 0 ? (
                          <Link href={`/content/health?site=${encodeURIComponent(s.siteId)}&kind=${k}`} aria-label={t("show", { kind: t(`kind.${k}`), site: pickName(s.names, locale) })}>
                            {number.format(count(s, k))}
                          </Link>
                        ) : (
                          number.format(0)
                        )}
                      </td>
                    ))}
                    <td>
                      {s.ownerless ? <Badge tone="warning">{t("ownerlessSite")}</Badge> : null}
                      {s.archived ? <Badge tone="neutral">{t("archivedLive", { count: s.liveLinks ?? 0 })}</Badge> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {list && kind ? (
              <section className="content-card" aria-labelledby="health-items">
                <h2 id="health-items">{t(`kind.${kind}`)}</h2>
                {!list.ok || list.data.length === 0 ? (
                  <StateView kind="empty" title={t("none")} message="" />
                ) : (
                  <ul className="content-list">
                    {list.data.map((i) => (
                      <li key={i.id}>
                        <Link href={editHref(i.id)}>
                          <bdi>{i.title}</bdi>
                        </Link>
                      </li>
                    ))}
                  </ul>
                )}
              </section>
            ) : null}
            {settings.ok ? <RetentionSettings settings={settings.data} /> : null}
          </>
        )}
      </main>
    </>
  );
}
