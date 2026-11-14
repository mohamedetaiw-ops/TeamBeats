import Link from "next/link";
import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pickName, type SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { Badge } from "@/components/ui/Badge";
import { StateView } from "@/components/ui/StateView";
import { SiteCreateForm } from "@/features/content/SiteForms";
import "@/features/content/content.css";

/**
 * Sites for content editors and site owners (spec 009 US6): the sites the person works in, each with its status, and a
 * form to create a site from a template. The API lists only the sites the person may work in and decides who may create.
 */
export default async function ContentSitesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [sites, units] = await Promise.all([apiGet<SiteView[]>("/api/v1/content/sites"), listUnits()]);
  const t = await getTranslations("content.sites");
  const locale = await getLocale();
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/content/sites" }]} />
      <main id="main" className="page content-manage">
        <h1>{t("title")}</h1>
        <p className="field-hint">{t("intro")}</p>
        {!sites.ok ? (
          <StateView kind="denied" title={t("denied")} message="" />
        ) : (
          <>
            <section aria-labelledby="site-list">
              <h2 id="site-list">{t("list")}</h2>
              <ul className="content-site-list">
                {sites.data.map((s) => {
                  const name = pickName(s.names, locale);
                  return (
                    <li key={s.id} className="content-card" data-testid="content-site">
                      <h3>
                        <bdi>{name}</bdi>
                      </h3>
                      <p className="content-meta">
                        <Badge tone={s.status === "active" ? "success" : "neutral"}>{t(`status.${s.status === "active" ? "active" : "archived"}`)}</Badge>
                        {s.isDefault ? <Badge tone="info">{t("default")}</Badge> : null}
                        {s.templateKey ? <span>{t(`templates.${s.templateKey as "subsidiary"}`)}</span> : null}
                      </p>
                      <Link href={`/content/sites/${encodeURIComponent(s.id)}`} aria-label={t("manage", { name })}>
                        {t("settings")}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </section>
            <SiteCreateForm units={units.ok ? units.data : []} />
          </>
        )}
      </main>
    </>
  );
}
