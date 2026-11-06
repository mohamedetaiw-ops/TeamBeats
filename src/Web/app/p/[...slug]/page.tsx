import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getTheme, renderPage } from "@/lib/experience/client";
import { Header } from "@/components/shell/Header";
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
  const theme = await getTheme(page.data.themeScope);
  const css = theme.ok ? theme.data.variants.filter((v) => v.density === "comfortable") : [];
  const t = await getTranslations("experience");
  return (
    <>
      {css.map((v) => (
        <link key={v.mode} rel="stylesheet" href={v.cssUrl} media={`(prefers-color-scheme: ${v.mode})`} precedence="theme" />
      ))}
      <Header me={me.data} />
      <main id="main">
        <PageRenderer page={page.data} tabsLabel={t("panels")} tabLabel={(n) => t("panel", { number: n })} />
      </main>
    </>
  );
}
