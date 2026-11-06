import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getTheme, renderPage } from "@/lib/experience/client";
import { Header } from "@/components/shell/Header";
import { ThemeLinks } from "@/components/shell/ThemeLinks";
import { PageRenderer } from "@/components/experience/PageRenderer";

/**
 * A published page (spec 032 US1 and US4): the API returns the effective configuration of the latest release for
 * this viewer, and the page uses the theme of the scope that owns it (spec 033). Pages the viewer may not see, and
 * pages of other tenants, are not found.
 */
export default async function PublishedPage({ params }: { params: Promise<{ slug: string[] }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { slug } = await params;
  const page = await renderPage(slug.map(decodeURIComponent).join("/"));
  if (!page.ok) {
    notFound();
  }
  // The layout already links the tenant theme; a page owned by an org unit adds the theme of that scope after it.
  const scoped = page.data.themeScope === "tenant" ? null : await getTheme(page.data.themeScope);
  const t = await getTranslations("experience");
  return (
    <>
      {scoped?.ok ? <ThemeLinks variants={scoped.data.variants} /> : null}
      <Header me={me.data} />
      <main id="main">
        <PageRenderer page={page.data} tabsLabel={t("panels")} tabLabel={(n) => t("panel", { number: n })} />
      </main>
    </>
  );
}
