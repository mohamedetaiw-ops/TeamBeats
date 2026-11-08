import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { editHref, variantFor, type ItemView, type SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { ItemEditor } from "@/features/content/ItemEditor";

/** Edits one content item (spec 009 T023). An item outside the person's sites is not found. */
export default async function ContentItemPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const item = await apiGet<ItemView>(`/api/v1/content/items/${encodeURIComponent(id)}`);
  if (!item.ok) {
    notFound();
  }
  const [site, units] = await Promise.all([apiGet<SiteView>(`/api/v1/content/sites/${encodeURIComponent(item.data.siteId)}`), listUnits()]);
  if (!site.ok) {
    notFound();
  }
  const t = await getTranslations("content.items");
  const locale = await getLocale();
  const title = variantFor(item.data, locale)?.title ?? "";
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("listTitle"), href: "/content/items" },
          { label: title, href: editHref(item.data.id) },
        ]}
      />
      <main id="main" className="page content-manage">
        <h1>
          <bdi>{title}</bdi>
        </h1>
        <ItemEditor initial={item.data} site={site.data} units={units.ok ? units.data : []} locales={me.data.tenant.enabledLocales} />
      </main>
    </>
  );
}
