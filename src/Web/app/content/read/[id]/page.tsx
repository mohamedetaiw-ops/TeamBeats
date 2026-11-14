import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ReaderView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { BodyView } from "@/features/content/BodyView";
import { ContentSocial } from "@/features/content/ContentSocial";
import { ItemActions } from "@/features/assistant/TextActions";
import { AskAgentLink } from "@/features/agents/AskAgentLink";
import { ViewBeacon } from "@/features/analytics/ViewBeacon";
import "@/features/assistant/assistant.css";
import "@/features/content/content.css";

/**
 * Reading a content item (spec 009 US5 scenario 2, US3 scenario 3, US9): the live version in the reader's language or
 * the fallback with a note, the expired and under review labels, then reactions and comments. Opening it counts as a view (spec 021). Items the reader may not
 * view are not found.
 */
export default async function ReadPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const item = await apiGet<ReaderView>(`/api/v1/content/items/${encodeURIComponent(id)}/view`);
  if (!item.ok) {
    notFound();
  }
  const t = await getTranslations("content.read");
  const locale = await getLocale();
  const v = item.data;
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "long" }).format(new Date(iso));
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("crumb"), href: `/content/read/${v.id}` }]} />
      <main id="main" className="page content-read">
        <article lang={v.locale} className="content-article">
          <h1>
            <bdi>{v.title}</bdi>
          </h1>
          <p className="content-meta">
            {t("updated", { date: date(v.updatedAt) })}
            {v.expired ? <span className="content-badge">{t("expired")}</span> : null}
            {v.underReview ? <span className="content-badge">{t("underReview")}</span> : null}
            {v.siteArchived ? <span className="content-badge">{t("siteArchived")}</span> : null}
          </p>
          {v.fallback ? (
            <p className="notice" data-testid="content-fallback">
              {t("fallback")}
            </p>
          ) : null}
          {v.summary ? (
            <p className="content-summary">
              <bdi>{v.summary}</bdi>
            </p>
          ) : null}
          <BodyView body={v.body} embedTitle={t("embed")} anchors={v.anchors} />
        </article>
        <ViewBeacon source="content" itemId={v.id} />
        <ItemActions source="content" itemId={v.id} itemLocale={v.locale} uiLocale={locale} />
        <AskAgentLink source="content" itemId={v.id} />
        <ContentSocial itemId={v.id} />
      </main>
    </>
  );
}
