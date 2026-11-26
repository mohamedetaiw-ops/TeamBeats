import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { eventCalendarHref, pickName, topicHref, type EventCard, type Inline, type ReaderView } from "@/lib/content/types";
import { Badge } from "@/components/ui/Badge";
import { FactList } from "@/components/ui/Patterns";
import { Widget } from "@/components/ui/Widget";
import { ReadingArticle } from "@/components/ui/Card";
import { Header } from "@/components/shell/Header";
import { BodyView } from "@/features/content/BodyView";
import { ContentSocial } from "@/features/content/ContentSocial";
import { ItemActions } from "@/features/assistant/TextActions";
import { AskAgentLink } from "@/features/agents/AskAgentLink";
import { ViewBeacon } from "@/features/analytics/ViewBeacon";
import "@/features/assistant/assistant.css";
import "@/features/content/content.css";

const count = (runs: readonly Inline[] | null | undefined) => (runs ?? []).reduce((n, r) => n + r.text.split(/\s+/u).filter(Boolean).length, 0);

/**
 * Reading a content item (spec 009 US5 scenario 2, US3 scenario 3, US9): the live version in the reader's language or
 * the fallback with a note, the expired and under review labels, then reactions and comments. Opening it counts as a view (spec 021). Items the reader may not
 * view are not found. The page leads with the story (spec 036 DLS-112): kind, title, a byline with date and reading
 * time, the quiet AI row, then the text; reactions and comments follow in one lighter block.
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
  const tk = await getTranslations("content.items.typeValue");
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: "long" }).format(new Date(iso));
  const words = v.body.blocks.reduce(
    (n, b) => n + count(b.children) + (b.items ?? []).reduce((m, i) => m + count(i), 0) + (b.rows ?? []).reduce((m, r) => m + r.reduce((k, c) => k + count(c), 0), 0),
    0,
  );
  const kind = (["news", "announcement", "page", "event"] as const).find((k) => k === v.type);
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("crumb"), href: `/content/read/${v.id}` }]} />
      <main id="main" className="page content-read">
        <ReadingArticle lang={v.locale}>
          <header className="content-head">
            {kind && kind !== "page" ? <Badge tone="accent">{tk(kind)}</Badge> : null}
            <h1>
              <bdi>{v.title}</bdi>
            </h1>
            <p className="content-meta content-byline">
              <span>{v.updatedAt.slice(0, 10) === v.publishedAt.slice(0, 10) ? t("published", { date: date(v.publishedAt) }) : t("updated", { date: date(v.updatedAt) })}</span>
              {words > 0 ? <span>{t("readTime", { minutes: Math.max(1, Math.round(words / 200)) })}</span> : null}
              {v.expired ? <Badge tone="warning">{t("expired")}</Badge> : null}
              {v.underReview ? <Badge tone="info">{t("underReview")}</Badge> : null}
              {v.siteArchived ? <Badge>{t("siteArchived")}</Badge> : null}
            </p>
            <div className="content-ai">
              <ItemActions source="content" itemId={v.id} itemLocale={v.locale} uiLocale={locale} />
              <AskAgentLink source="content" kind={v.type === "knowledge" ? "knowledge" : "content"} itemId={v.id} />
            </div>
          </header>
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
          {v.event ? <EventCardView card={v.event} locale={locale} t={t} /> : null}
          <BodyView body={v.body} embedTitle={t("embed")} anchors={v.anchors} topics />
          {v.tags && v.tags.length > 0 ? (
            <ul className="content-tags" aria-label={t("tags")}>
              {v.tags.map((tag) => (
                <li key={tag}>
                  <Link href={topicHref(tag)}>
                    <bdi>{t("tag", { tag })}</bdi>
                  </Link>
                </li>
              ))}
            </ul>
          ) : null}
        </ReadingArticle>
        <ViewBeacon source="content" itemId={v.id} contentType={v.type} siteId={v.siteId} />
        <ContentSocial itemId={v.id} />
      </main>
    </>
  );
}

/**
 * The calendar event of an event item (spec 009 FR-018 R3): when and where, the online link, and the reader's own
 * registration with a link to the calendar where they register. The facts come from the calendar on every read.
 */
function EventCardView({ card, locale, t }: { card: EventCard; locale: string; t: Awaited<ReturnType<typeof getTranslations<"content.read">>> }) {
  const when = card.allDay
    ? new Intl.DateTimeFormat(locale, { dateStyle: "full", timeZone: "UTC" }).format(new Date(`${card.startDate ?? ""}T00:00:00Z`))
    : new Intl.DateTimeFormat(locale, { dateStyle: "full", timeStyle: "short", timeZone: card.timeZone }).format(new Date(card.start ?? ""));
  const r = card.registration;
  const state = r?.state === "registered" ? t("event.registered") : r?.state === "waiting" ? t("event.waiting", { position: r.position ?? 0 }) : null;
  const facts = [
    { key: "when", label: t("event.when"), value: when },
    ...(card.location ? [{ key: "where", label: t("event.where"), value: <bdi>{card.location}</bdi> }] : []),
    ...(card.onlineUrl
      ? [
          {
            key: "online",
            label: t("event.online"),
            value: (
              <a href={card.onlineUrl} target="_blank" rel="noopener noreferrer">
                {t("event.join")}
              </a>
            ),
          },
        ]
      : []),
    ...(r ? [{ key: "registration", label: t("event.registration"), value: state ?? (r.open ? t("event.seats", { count: r.seatsLeft }) : t("event.closed")) }] : []),
  ];
  return (
    <Widget
      title={pickName(card.titles, locale)}
      count={card.status !== "published" ? <Badge tone="warning">{t("event.cancelled")}</Badge> : undefined}
      testId="content-event"
      footer={card.status === "published" ? <Link href={eventCalendarHref}>{r && r.open && !r.state ? t("event.register") : t("event.inCalendar")}</Link> : undefined}
    >
      <FactList facts={facts} />
    </Widget>
  );
}
