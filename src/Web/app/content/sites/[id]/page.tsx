import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pickName, type SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { SiteSettings } from "@/features/content/SiteForms";
import { RouteSettings } from "@/features/content/RouteSettings";
import "@/features/content/content.css";

/** One site's settings for its owner (spec 009 US6). Someone who may not manage the site gets not found from the API. */
export default async function ContentSitePage({ params }: { params: Promise<{ id: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { id } = await params;
  const [site, units] = await Promise.all([apiGet<SiteView>(`/api/v1/content/sites/${encodeURIComponent(id)}`), listUnits()]);
  if (!site.ok) {
    notFound();
  }
  const t = await getTranslations("content.sites");
  const locale = await getLocale();
  const name = pickName(site.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/content/sites" },
          { label: name, href: `/content/sites/${encodeURIComponent(id)}` },
        ]}
      />
      <main id="main" className="page content-manage">
        <h1>
          <bdi>{name}</bdi>
        </h1>
        <SiteSettings site={site.data} units={units.ok ? units.data : []} locales={me.data.tenant.enabledLocales} />
        <RouteSettings key={site.data.version} site={site.data} />
      </main>
    </>
  );
}
