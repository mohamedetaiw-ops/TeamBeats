"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { readHref, type FeedItem, type FeedPage } from "@/lib/content/types";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
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
    <section className="content-card" aria-label={title} aria-busy={busy || undefined}>
      <h2>{title}</h2>
      {body}
    </section>
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
  return shell(
    <ul className="content-list">
      {answer.map((item) => (
        <li key={item.id} className="content-list-item" lang={item.locale}>
          <Link href={readHref(item.id)}>
            <bdi>{item.title}</bdi>
          </Link>
          {item.pinned ? <span className="content-badge">{t("pinned")}</span> : null}
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
