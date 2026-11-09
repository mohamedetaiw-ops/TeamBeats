import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { editHref } from "@/lib/content/types";
import { itemAnalyticsApi, type ItemAnalyticsView } from "@/lib/analytics/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { ItemAnalyticsPanel } from "@/features/analytics/ItemAnalyticsPanel";
import "@/features/content/content.css";

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

/** Reach and acknowledgement of a content item (spec 021 US1, spec 009), for the people who look after it. */
export default async function ContentAnalyticsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<ItemAnalyticsView>(itemAnalyticsApi("content", id));
  if (!view.ok && view.status !== 403) {
    notFound();
  }
  const t = await getTranslations("analytics.item");
  const tc = await getTranslations("content.items");
  const locale = await getLocale();
  const title = view.ok ? pick(view.data.titles, locale) : "";
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: tc("listTitle"), href: "/content/items" },
          ...(view.ok ? [{ label: title, href: editHref(id) }] : []),
          { label: t("title"), href: `${editHref(id)}/analytics` },
        ]}
      />
      <main id="main" className="page content-manage">
        {view.ok ? (
          <>
            <h1>
              {t("heading")} <bdi>{title}</bdi>
            </h1>
            <ItemAnalyticsPanel source="content" view={view.data} />
          </>
        ) : (
          <StateView kind="denied" message={t("denied")} />
        )}
      </main>
    </>
  );
}
