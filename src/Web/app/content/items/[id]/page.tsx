import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { editHref, variantFor, type ItemView, type SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { ItemEditor } from "@/features/content/ItemEditor";
import { SlackShare, type SlackChannelOption } from "@/features/content/SlackShare";

/** Edits one content item (spec 009 T023), and shares a live one to Slack (spec 017 US8). An item outside the person's sites is not found. */
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
  // Slack channels load only for a live item; the API answers 403 or 409 when the person may not share or Slack is not connected.
  const [site, units, slack] = await Promise.all([
    apiGet<SiteView>(`/api/v1/content/sites/${encodeURIComponent(item.data.siteId)}`),
    listUnits(),
    item.data.status === "published" ? apiGet<{ channels: SlackChannelOption[] }>("/api/v1/content/items/slack-channels") : Promise.resolve(null),
  ]);
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
        {slack?.ok && slack.data.channels.length > 0 ? <SlackShare itemId={item.data.id} channels={slack.data.channels} /> : null}
      </main>
    </>
  );
}
