import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import { getTheme, renderPage } from "@/lib/experience/client";
import type { RenderedPage, RenderedRegion } from "@/lib/experience/types";
import { getPreferences, getShell } from "@/lib/shell/client";
import { Header } from "@/components/shell/Header";
import { ThemeLinks } from "@/components/shell/ThemeLinks";
import { PageRenderer } from "@/components/experience/PageRenderer";
import { PersonalizationProvider } from "@/features/personalization/Personalization";
import { PlacementTools, RegionTools } from "@/features/personalization/Tools";
import type { LayoutView } from "@/features/personalization/types";
import { SectionNav } from "@/components/ui/SectionNav";
import type { HomeView } from "@/lib/shell/types";
import "./home-views.css";

/**
 * The employee home (spec 008 US2): the page the shell names as home, with the user's own layout over it and
 * personalization mode in the areas the policy delegates. Without a published home the root keeps a plain welcome.
 * When the tenant offers more home views to this viewer (spec 008 FR-058), they follow My day as tabs, and `?view=`
 * opens one; a view the viewer does not get falls back to My day.
 */
export default async function Home({ searchParams }: { searchParams: Promise<{ view?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const shell = await getShell();
  const views = shell.ok ? (shell.data.homeViews ?? []) : [];
  const { view: requested } = await searchParams;
  const view = views.find((v) => v.id === requested) ?? null;
  const slug = shell.ok ? (view?.slug ?? shell.data.home) : null;
  const page = slug ? await renderPage(slug) : null;
  if (page?.ok) {
    return <HomePage me={me.data} page={page.data} views={views} current={view?.id ?? null} />;
  }
  const t = await getTranslations("home");
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  return (
    <>
      <Header me={me.data} />
      <main id="main" className="page">
        <h1>
          {t("title", { name: me.data.displayName })}
        </h1>
        <p>
          {t("subtitle", { tenant: me.data.tenant.key })}
        </p>
        {nav.ok && nav.data.length > 0 ? (
          <Link className="button" href="/admin">
            {t("openAdmin")}
          </Link>
        ) : null}
      </main>
    </>
  );
}

async function HomePage({ me, page, views, current }: { me: Me; page: RenderedPage; views: HomeView[]; current: string | null }) {
  const [layout, prefs, t, te] = await Promise.all([
    apiGet<LayoutView>(`/api/v1/me/pages/${page.pageId}/layout`),
    getPreferences(),
    getTranslations("home"),
    getTranslations("experience"),
  ]);
  const scoped = page.themeScope === "tenant" ? null : await getTheme(page.themeScope);
  const rights = (r: RenderedRegion) => r.rights ?? [];
  const targets = page.regions.map((r) => ({ id: r.id, rights: rights(r) }));
  return (
    <>
      {scoped?.ok ? (
        <ThemeLinks variants={scoped.data.variants} density={prefs.density === "compact" ? "compact" : "comfortable"} mode={prefs.mode === "light" || prefs.mode === "dark" ? prefs.mode : "system"} />
      ) : null}
      <Header me={me} />
      <main id="main">
        <h1 className="visually-hidden">{t("pageTitle")}</h1>
        {views.length > 0 ? (
          <div className="home-views">
            <SectionNav
              label={t("views.label")}
              current={current ?? "mine"}
              items={[{ key: "mine", href: "/", label: t("views.mine") }, ...views.map((v) => ({ key: v.id, href: `/?view=${encodeURIComponent(v.id)}`, label: v.label }))]}
            />
          </div>
        ) : null}
        <PersonalizationProvider
          key={page.pageId}
          pageId={page.pageId}
          regions={page.regions.map((r) => r.id)}
          initial={layout.ok ? { operations: layout.data.operations, eTag: layout.data.eTag } : { operations: [], eTag: "" }}
          canPersonalize={layout.ok && page.regions.some((r) => rights(r).length > 0)}
        >
          <PageRenderer
            page={page}
            tabsLabel={te("panels")}
            tabLabel={(n) => te("panel", { number: n })}
            tools={{
              region: (region) => <RegionTools regionId={region.id} rights={rights(region)} />,
              placement: (placement, region, index) => (
                <PlacementTools
                  placementId={placement.id}
                  regionId={region.id}
                  index={index}
                  count={region.placements.length}
                  personal={placement.personal}
                  rights={rights(region)}
                  targets={targets}
                  neighbor={region.placements[index + 1]?.id ?? region.placements[index - 1]?.id ?? null}
                />
              ),
            }}
          />
        </PersonalizationProvider>
      </main>
    </>
  );
}
