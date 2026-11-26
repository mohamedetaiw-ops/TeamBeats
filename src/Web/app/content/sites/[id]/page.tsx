import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pickName, type ScreenView, type SiteGuestShare, type SiteView } from "@/lib/content/types";
import { spacesApi, type SpaceSummary } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { SiteSettings } from "@/features/content/SiteForms";
import { RouteSettings } from "@/features/content/RouteSettings";
import { ScreenSettings } from "@/features/content/ScreenSettings";
import { SiteGuestShares } from "@/features/content/SiteGuestShares";
import "@/features/content/content.css";

/** One site's settings for its owner (spec 009 US6). Someone who may not manage the site gets not found from the API. */
export default async function ContentSitePage({ params }: { params: Promise<{ id: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { id } = await params;
  const [site, units, screens, guestShares, spaces] = await Promise.all([
    apiGet<SiteView>(`/api/v1/content/sites/${encodeURIComponent(id)}`),
    listUnits(),
    apiGet<ScreenView[]>(`/api/v1/content/sites/${encodeURIComponent(id)}/screens`),
    // Sites shared with guest spaces (spec 009 R3 US10); a package without them answers with a refusal and nothing shows.
    apiGet<SiteGuestShare[]>(`/api/v1/content/sites/${encodeURIComponent(id)}/guest-shares`),
    apiGet<SpaceSummary[]>(spacesApi),
  ]);
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
        <RouteSettings site={site.data} />
        {screens.ok ? <ScreenSettings site={site.data} screens={screens.data} /> : null}
        {guestShares.ok ? (
          <SiteGuestShares
            site={site.data}
            shares={guestShares.data}
            spaces={spaces.ok ? spaces.data.filter((s) => s.role === "owner" && s.status === "active").map((s) => ({ id: s.id, name: s.name })) : []}
          />
        ) : null}
      </main>
    </>
  );
}
