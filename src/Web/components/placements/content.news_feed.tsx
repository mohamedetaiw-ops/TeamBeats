"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { readHref, type FeedItem, type FeedPage } from "@/lib/content/types";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import "@/features/content/content.css";

type Answer = "loading" | "failed" | FeedItem[];

/**
 * The news feed component (spec 009 FR-030): the newest live items the viewer may read, pinned first. The feed API
 * applies audience and permission checks, so the component only shows what it gets back.
 */
export default function NewsFeed({ props }: PlacementProps) {
  const t = useTranslations("content.component.feed");
  const locale = useLocale();
  const format = useFormatter();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 20 ? Math.floor(props.top) : 5;
  const type = typeof props.type === "string" && props.type !== "all" ? props.type : null;
  const site = typeof props.site === "string" && props.site ? props.site : null;
  const title = localized(props.title, locale) || t("title");
  const query = new URLSearchParams({ limit: String(top) });
  if (type) query.set("type", type);
  if (site) query.set("site", site);
  const url = `/api/v1/content/feed?${query.toString()}`;
  const [answer, setAnswer] = useState<Answer>("loading");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    fetch(url)
      .then(async (r) => {
        if (!r.ok) throw new Error(String(r.status));
        const page = (await r.json()) as FeedPage;
        if (live) setAnswer(page.items);
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [url, attempt]);

  const shell = (body: React.ReactNode, busy = false) => (
    <Widget title={title} busy={busy}>
      {body}
    </Widget>
  );

  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") {
    const retry = (
      <Button
        onClick={() => {
          setAnswer("loading");
          setAttempt((a) => a + 1);
        }}
      >
        {t("retry")}
      </Button>
    );
    return shell(<StateView kind="error" title={t("failed")} message="" action={retry} />);
  }
  if (answer.length === 0) return shell(<StateView kind="empty" title={t("none")} message="" />);
  if (props.layout === "featured") return <Featured items={answer} title={title} />;
  return shell(
    <ul className="content-list">
      {answer.map((item) => (
        <li key={item.id} className="content-list-item" lang={item.locale}>
          <Link href={readHref(item.id)}>
            <bdi>{item.title}</bdi>
          </Link>
          {item.pinned ? <Badge tone="accent">{t("pinned")}</Badge> : null}
          {item.summary ? (
            <p className="content-meta">
              <bdi>{item.summary}</bdi>
            </p>
          ) : null}
          <p className="content-meta">
            <time dateTime={item.publishedAt}>{format.dateTime(new Date(item.publishedAt), { dateStyle: "medium" })}</time>
          </p>
        </li>
      ))}
    </ul>,
  );
}

/**
 * Featured news (docs/design-system/components.md, Featured news): the lead story (featured first, then pinned, then
 * newest) as a large card with its type, reading time and reactions, and the next stories as compact rows beside it.
 */

function Featured({ items, title }: { items: FeedItem[]; title: string }) {
  const t = useTranslations("content.component.feed");
  const format = useFormatter();
  const lead = items.find((i) => i.featured) ?? items.find((i) => i.pinned) ?? items[0]!;
  const rest = items.filter((i) => i.id !== lead.id);
  return (
    <section className="content-featured" aria-label={title}>
      <h2 className="visually-hidden">{title}</h2>
      <article className="content-lead" lang={lead.locale}>
        {/* Stories carry no photo yet (spec 009), so the lead is a typographic card in its type's tone (DLS-088). */}
        <div className="content-lead-media content-lead-type" data-tone={lead.type === "announcement" ? "highlight" : "accent"} aria-hidden="true">
          <Icon name={lead.type === "announcement" ? "megaphone" : "news"} size={40} />
        </div>
        <div className="content-lead-body">
          <p className="content-lead-eyebrow">
            <span>{t(`type.${lead.type === "announcement" ? "announcement" : "news"}`)}</span>
            <span>{t("readTime", { minutes: lead.readMinutes })}</span>
          </p>
          <h3>
            <Link className="content-lead-link" href={readHref(lead.id)}>
              <bdi>{lead.title}</bdi>
            </Link>
          </h3>
          {lead.summary ? (
            <p className="content-meta">
              <bdi>{lead.summary}</bdi>
            </p>
          ) : null}
          <p className="content-lead-meta">
            <time dateTime={lead.publishedAt}>{format.dateTime(new Date(lead.publishedAt), { dateStyle: "medium" })}</time>
            {lead.reactions > 0 ? <span>{t("reactions", { count: lead.reactions })}</span> : null}
          </p>
        </div>
      </article>
      {rest.length > 0 ? (
        <ul className="content-side-list">
          {rest.map((item) => (
            <li key={item.id} className="content-side-item" lang={item.locale}>
              <span className="ui-icon-tile" data-tone={item.pinned ? "highlight" : "accent"}>
                <Icon name="news" size={20} />
              </span>
              <div>
                <p className="content-side-eyebrow">
                  {item.pinned ? t("pinned") : t(`type.${item.type === "announcement" ? "announcement" : "news"}`)}
                </p>
                <Link className="content-side-link" href={readHref(item.id)}>
                  <bdi>{item.title}</bdi>
                </Link>
                <p className="content-meta">
                  <time dateTime={item.publishedAt}>{format.dateTime(new Date(item.publishedAt), { dateStyle: "medium" })}</time>
                  {" · "}
                  {t("readTime", { minutes: item.readMinutes })}
                </p>
              </div>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
