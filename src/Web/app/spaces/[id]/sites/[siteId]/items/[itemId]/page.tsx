import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pickName, sharedItemHref, sharedSiteHref, type GuestItemView } from "@/lib/content/types";
import { isGuid, spaceHref, spacesApi, spacesHref, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { BodyView } from "@/features/content/BodyView";
import "@/features/content/content.css";
import "@/features/spaces/spaces.css";

/**
 * An item of an intranet site shared with a space (spec 009 R3 US10), as a guest reads it: the title, date and text,
 * with no comments, reactions or confirmation. The API returns only published Public and Internal items of a live share.
 */
export default async function SharedItemPage({ params }: { params: Promise<{ id: string; siteId: string; itemId: string }> }) {
  const { id, siteId, itemId } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id) || !isGuid(siteId) || !isGuid(itemId)) {
    notFound();
  }
  const [space, item] = await Promise.all([
    apiGet<SpaceView>(`${spacesApi}/${id}`),
    apiGet<GuestItemView>(`/api/v1/content/shared-with-space/${id}/sites/${siteId}/items/${itemId}`),
  ]);
  if (!space.ok || !item.ok) {
    notFound();
  }
  const [t, locale, format] = await Promise.all([getTranslations("spaces.sharedSites"), getLocale(), getFormatter()]);
  const i = item.data;
  const siteName = pickName(i.siteNames, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("spaces"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: siteName, href: sharedSiteHref(id, siteId) },
          { label: i.title, href: sharedItemHref(id, siteId, itemId) },
        ]}
      />
      <main id="main" className="page" lang={i.locale}>
        <PageHeader title={i.title} eyebrow={siteName} description={t("published", { date: format.dateTime(new Date(i.publishedAt), { dateStyle: "long" }) })} icon="news" />
        <article className="spaces-shared-item" data-testid="shared-item">
          {i.summary ? (
            <p>
              <strong>
                <bdi>{i.summary}</bdi>
              </strong>
            </p>
          ) : null}
          <BodyView body={i.body} embedTitle={t("embed")} />
        </article>
      </main>
    </>
  );
}
