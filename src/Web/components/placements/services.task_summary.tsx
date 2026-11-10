"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { pick, type InboxItem } from "@/lib/services/types";
import { useNow } from "@/lib/clock";
import { Button } from "@/components/ui/Button";
import { StateView } from "@/components/ui/StateView";
import { isolate } from "@/lib/bidi";
import "@/features/services/home.css";

type Answer = "loading" | "failed" | InboxItem[];

const DAY = 86_400_000;

/**
 * The task inbox summary (spec 016 home component, spec 008 scope): how many tasks wait for the viewer, how many are
 * overdue or due within a day, and the oldest few with a link to each. The inbox API returns only the viewer's tasks.
 */
export default function TaskSummary({ props }: PlacementProps) {
  const t = useTranslations("services.home.tasks");
  const locale = useLocale();
  const format = useFormatter();
  const now = useNow();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 3;
  const title = localized(props.title, locale) || t("title");
  const [answer, setAnswer] = useState<Answer>("loading");
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    fetch("/api/v1/tasks")
      .then(async (r) => {
        if (!r.ok) throw new Error(String(r.status));
        const items = (await r.json()) as InboxItem[];
        if (live) setAnswer(items);
      })
      .catch(() => live && setAnswer("failed"));
    return () => {
      live = false;
    };
  }, [attempt]);

  const shell = (body: React.ReactNode, busy = false) => (
    <section className="home-card" aria-labelledby="home-tasks-title" aria-busy={busy || undefined} data-testid="home-task-summary">
      <div className="home-card-head">
        <h2 id="home-tasks-title">{title}</h2>
        <Link href="/tasks">{t("inbox")}</Link>
      </div>
      {body}
    </section>
  );

  if (answer === "loading" || now === null) return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
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
  const overdue = answer.filter((i) => i.dueAt && Date.parse(i.dueAt) < now).length;
  const soon = answer.filter((i) => i.dueAt && Date.parse(i.dueAt) >= now && Date.parse(i.dueAt) < now + DAY).length;
  const oldest = [...answer].sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt)).slice(0, top);
  const name = (i: InboxItem) => (i.serviceNames ? pick(i.serviceNames, locale) : i.title ? pick(i.title, locale) : i.name);
  const href = (i: InboxItem) => (i.requestId ? `/requests/${i.requestId}?task=${i.taskId}` : i.link && /^\/(?!\/)/.test(i.link) ? i.link : "/tasks");
  return shell(
    <>
      <ul className="home-stats">
        <li data-tone="warm">
          <strong>{format.number(answer.length)}</strong>
          <span>{t("waiting", { count: answer.length })}</span>
        </li>
        <li data-tone="accent">
          <strong>{format.number(overdue)}</strong>
          <span>{t("overdue", { count: overdue })}</span>
        </li>
        <li data-tone="fresh">
          <strong>{format.number(soon)}</strong>
          <span>{t("dueSoon", { count: soon })}</span>
        </li>
      </ul>
      {oldest.length === 0 ? (
        <p>{t("none")}</p>
      ) : (
        <ul className="home-list">
          {oldest.map((i) => (
            <li key={i.taskId}>
              <Link href={href(i)}>
                <bdi>{name(i)}</bdi>
              </Link>
              <span className="home-meta">
                {i.requester ? t("from", { name: isolate(i.requester.displayName) }) : null}
                {i.requester ? " · " : null}
                {t("age", { when: format.relativeTime(new Date(i.createdAt), now) })}
              </span>
            </li>
          ))}
        </ul>
      )}
    </>,
  );
}
