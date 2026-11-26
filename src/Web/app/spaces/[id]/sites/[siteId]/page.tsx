import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pickName, sharedItemHref, sharedSiteHref, type GuestItemSummary, type SharedSite } from "@/lib/content/types";
import { isGuid, spaceHref, spacesApi, spacesHref, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/spaces/spaces.css";

/**
 * An intranet site shared with a space (spec 009 R3 US10): its published Public and Internal items, newest first, for
 * the space's readers. The API decides what is shared and for how long; nothing else of the intranet opens here.
 */
export default async function SharedSitePage({ params }: { params: Promise<{ id: string; siteId: string }> }) {
  const { id, siteId } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id) || !isGuid(siteId)) {
    notFound();
  }
  const [space, shared, items] = await Promise.all([
    apiGet<SpaceView>(`${spacesApi}/${id}`),
    apiGet<SharedSite[]>(`/api/v1/content/shared-with-space/${id}`),
    apiGet<GuestItemSummary[]>(`/api/v1/content/shared-with-space/${id}/sites/${siteId}/items`),
  ]);
  const site = shared.ok ? shared.data.find((s) => s.siteId === siteId) : undefined;
  if (!space.ok || !site || !items.ok) {
    notFound();
  }
  const [t, locale, format] = await Promise.all([getTranslations("spaces.sharedSites"), getLocale(), getFormatter()]);
  const name = pickName(site.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("spaces"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: name, href: sharedSiteHref(id, siteId) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={name} eyebrow={space.data.name} description={t("siteIntro")} icon="news" />
        {items.data.length === 0 ? (
          <StateView kind="empty" title={t("empty")} message="" />
        ) : (
          <Widget title={t("items")}>
            <ul className="spaces-shared-sites" data-testid="shared-site-items">
              {items.data.map((i) => (
                <li key={i.id} lang={i.locale} className="spaces-shared-site">
                  <Link href={sharedItemHref(id, siteId, i.id)}>
                    <bdi>{i.title}</bdi>
                  </Link>
                  <span className="field-hint">{format.dateTime(new Date(i.publishedAt), { dateStyle: "medium" })}</span>
                  {i.summary ? (
                    <span>
                      <bdi>{i.summary}</bdi>
                    </span>
                  ) : null}
                </li>
              ))}
            </ul>
          </Widget>
        )}
      </main>
    </>
  );
}
