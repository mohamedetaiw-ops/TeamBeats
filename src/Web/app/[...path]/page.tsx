import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getTheme, renderPage } from "@/lib/experience/client";
import { getPreferences, resolvePath } from "@/lib/shell/client";
import { Header } from "@/components/shell/Header";
import { ThemeLinks } from "@/components/shell/ThemeLinks";
import { PageRenderer } from "@/components/experience/PageRenderer";
import { StateView } from "@/components/ui/StateView";
import { SectionMenu } from "@/features/shell/Shell";

/**
 * An address in the information architecture (spec 008 FR-013, FR-014, FR-019). The API maps it to a page with
 * breadcrumbs and a section menu, to a redirect (an old slug or a link entry), or to the recovery answer, which is
 * the same for missing, unpublished and not permitted destinations, so the page never reveals whether one exists.
 */
export default async function AddressPage({ params }: { params: Promise<{ path: string[] }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { path } = await params;
  const resolved = await resolvePath(path.map(decodeURIComponent).join("/"));
  if (resolved.ok && resolved.data.kind === "redirect" && resolved.data.redirectTo) {
    redirect(resolved.data.redirectTo);
  }
  const t = await getTranslations("shell");
  const page = resolved.ok && resolved.data.kind === "page" && resolved.data.pageSlug ? await renderPage(resolved.data.pageSlug) : null;
  if (!resolved.ok || !page?.ok) {
    return (
      <>
        <Header me={me.data} />
        <main id="main" className="page">
          <h1>{t("recovery.title")}</h1>
          <StateView
            kind="empty"
            title={t("recovery.message")}
            message=""
            action={
              <Link className="button" href="/">
                {t("recovery.home")}
              </Link>
            }
          />
        </main>
      </>
    );
  }
  const prefs = await getPreferences();
  const scoped = page.data.themeScope === "tenant" ? null : await getTheme(page.data.themeScope);
  const te = await getTranslations("experience");
  return (
    <>
      {scoped?.ok ? (
        <ThemeLinks variants={scoped.data.variants} density={prefs.density === "compact" ? "compact" : "comfortable"} mode={prefs.mode === "light" || prefs.mode === "dark" ? prefs.mode : "system"} />
      ) : null}
      <Header me={me.data} breadcrumbs={resolved.data.breadcrumbs} />
      <SectionMenu label={t("sectionNav")} links={resolved.data.sectionMenu} />
      <main id="main">
        <PageRenderer page={page.data} tabsLabel={te("panels")} tabLabel={(n) => te("panel", { number: n })} />
      </main>
    </>
  );
}
