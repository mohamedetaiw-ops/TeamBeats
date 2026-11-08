"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { useFormatter, useLocale, useNow, useTranslations } from "next-intl";
import { StateView } from "@/components/ui/StateView";
import { pick, type InboxItem } from "@/lib/services/types";
import "./services.css";

/**
 * The task inbox (spec 016 US3, FR-007): everything waiting for the signed-in person, from form services and other
 * sources, with type, requester, age and due date, filterable by type. Each task opens its request to decide.
 */
export function TaskInbox({ items }: { items: InboxItem[] }) {
  const t = useTranslations("services.inbox");
  const locale = useLocale();
  const format = useFormatter();
  const now = useNow();
  const [type, setType] = useState("");

  const types = useMemo(() => {
    const seen = new Map<string, string>();
    for (const i of items) {
      const key = i.serviceId ?? i.source;
      if (!seen.has(key)) seen.set(key, i.serviceNames ? pick(i.serviceNames, locale) : sourceName(i));
    }
    return [...seen.entries()];
  }, [items, locale]);
  function sourceName(i: InboxItem) {
    return i.source === "content_review" ? t("contentReview") : i.name;
  }
  const shown = items.filter((i) => !type || (i.serviceId ?? i.source) === type);

  return (
    <section className="services-section" aria-labelledby="inbox-title">
      <h2 id="inbox-title">{t("title", { count: items.length })}</h2>
      {types.length > 1 ? (
        <label className="services-select">
          {t("type")}
          <select value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">{t("allTypes")}</option>
            {types.map(([key, name]) => (
              <option key={key} value={key}>
                {name}
              </option>
            ))}
          </select>
        </label>
      ) : null}
      {shown.length === 0 ? (
        <StateView kind="empty" title={t("empty")} message="" />
      ) : (
        <ul className="services-list" data-testid="task-inbox">
          {shown.map((i) => {
            const title = i.serviceNames ? pick(i.serviceNames, locale) : i.title ? pick(i.title, locale) : sourceName(i);
            return (
              <li key={i.taskId} className="services-item">
                {i.requestId ? (
                  <Link href={`/requests/${i.requestId}?task=${i.taskId}`}>
                    <bdi>{title}</bdi>
                    {i.number ? (
                      <>
                        {" · "}
                        <bdi dir="ltr">{i.number}</bdi>
                      </>
                    ) : null}
                  </Link>
                ) : (
                  <bdi>{title}</bdi>
                )}
                <span className="services-meta">
                  {i.step ? <bdi>{pick(i.step, locale)}</bdi> : i.title ? <bdi>{sourceName(i)}</bdi> : null}
                  {i.requester ? <> · {t("from", { name: i.requester.displayName })}</> : null}
                  {i.requestedFor ? <> · {t("for", { name: i.requestedFor.displayName })}</> : null}
                </span>
                <span className="services-meta">
                  {t("age", { when: format.relativeTime(new Date(i.createdAt), now) })}
                  {i.dueAt ? <> · {t("due", { when: format.dateTime(new Date(i.dueAt), { dateStyle: "medium" }) })}</> : null}
                </span>
                {i.onBehalfOf ? <span className="services-status">{t("onBehalfOf", { name: i.onBehalfOf.displayName })}</span> : null}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
