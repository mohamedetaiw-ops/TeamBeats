import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { editHref, variantFor, type ItemView, type VersionSummary } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { VersionHistory } from "@/features/content/VersionHistory";

/** Versions, comparison and restore of one item (spec 009 US4). */
export default async function ContentHistoryPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [item, versions] = await Promise.all([
    apiGet<ItemView>(`/api/v1/content/items/${encodeURIComponent(id)}`),
    apiGet<VersionSummary[]>(`/api/v1/content/items/${encodeURIComponent(id)}/versions`),
  ]);
  if (!item.ok || !versions.ok) {
    notFound();
  }
  const t = await getTranslations("content");
  const locale = await getLocale();
  const title = variantFor(item.data, locale)?.title ?? "";
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("items.listTitle"), href: "/content/items" },
          { label: title, href: editHref(item.data.id) },
          { label: t("history.title"), href: `${editHref(item.data.id)}/history` },
        ]}
      />
      <main id="main" className="page content-manage">
        <h1>{t("history.heading", { title })}</h1>
        <VersionHistory itemId={item.data.id} versions={versions.data} locales={Object.keys(item.data.variants)} primary={item.data.primaryLocale} />
      </main>
    </>
  );
}
