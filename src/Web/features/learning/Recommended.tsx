"use client";

import Link from "next/link";
import { reportOpened } from "@/lib/analytics/recommendations";
import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { call, courseHref, learningApi, localText, send, type RecommendedCourse } from "@/lib/learning";
import "./learning.css";

/**
 * Courses for the skills the learner has (spec 028 R3 US9): what the API recommends, with the skills each is tagged
 * with, and a way to say no. A dismissed course is not recommended again.
 */
export function Recommended({ initial }: { initial: RecommendedCourse[] }) {
  const t = useTranslations("learning.recommended");
  const locale = useLocale();
  const [items, setItems] = useState(initial);
  const [messages, setMessages] = useState<string[]>([]);

  async function dismiss(id: string) {
    setMessages([]);
    const result = await call<null>(async () => {
      const response = await send(`${learningApi}/recommendations/${id}:dismiss`, "POST");
      return response.status === 204 ? new Response("null", { status: 200 }) : response;
    }, t("failed"));
    if (result.ok) setItems((list) => list.filter((c) => c.id !== id));
    else setMessages(result.messages);
  }

  if (items.length === 0) return null;
  return (
    <section className="learning learning-section" aria-labelledby="learning-recommended">
      <h2 id="learning-recommended">{t("title")}</h2>
      <p className="learning-meta">{t("hint")}</p>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <ul className="learning-list" aria-label={t("title")}>
        {items.map((course) => {
          const title = localText(course.titles, locale);
          return (
            <li key={course.id}>
              <div className="learning-row">
                <Link href={courseHref(course.id)} onClick={() => reportOpened("learning")}>
                  <bdi>{title}</bdi>
                </Link>
                {course.skills.map((s) => (
                  <Badge key={s.id} tone="info">
                    <bdi>{localText(s.names, locale)}</bdi>
                  </Badge>
                ))}
                <Button size="xs" variant="quiet" icon="close" aria-label={t("dismissCourse", { title })} onClick={() => void dismiss(course.id)}>
                  {t("dismiss")}
                </Button>
              </div>
              {localText(course.summaries, locale) ? <p className="learning-meta">{localText(course.summaries, locale)}</p> : null}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
