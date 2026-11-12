"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { localized, type PlacementProps } from "@/lib/experience/contract";
import { useHomeAnswer } from "@/lib/engagementHome";
import { courseHref, learningApi, learningHref, localText, type MyLearning } from "@/lib/learning";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Progress } from "@/components/ui/Status";
import { StateView } from "@/components/ui/StateView";
import "@/features/services/home.css";

/**
 * My learning card (spec 028 T005): the viewer's open courses, the ones due first at the top, with how far they are and
 * a link to My learning. It reads the learning API with the viewer's session, so it shows only their own assignments.
 */
export default function MyLearningCard({ props }: PlacementProps) {
  const t = useTranslations("learning.home");
  const locale = useLocale();
  const format = useFormatter();
  const top = typeof props.top === "number" && props.top >= 1 && props.top <= 10 ? Math.floor(props.top) : 3;
  const title = localized(props.title, locale) || t("title");
  const { answer, retry } = useHomeAnswer<MyLearning>(`${learningApi}/me`);

  const shell = (body: React.ReactNode, busy = false) => (
    <section className="home-card" aria-labelledby="home-learning-title" aria-busy={busy || undefined}>
      <div className="home-card-head">
        <h2 id="home-learning-title">{title}</h2>
        <Link href={learningHref}>{t("all")}</Link>
      </div>
      {body}
    </section>
  );

  if (answer === "loading") return shell(<StateView kind="loading" minBlockSize="8rem" />, true);
  if (answer === "failed") return shell(<StateView kind="error" title={t("failed")} message="" action={<Button onClick={retry}>{t("retry")}</Button>} />);
  const open = answer === "off" ? [] : [...answer.assigned, ...answer.inProgress];
  // Due first, the nearest date at the top; courses without a date after them.
  open.sort((a, b) => (a.dueOn ?? "9999-12-31").localeCompare(b.dueOn ?? "9999-12-31"));
  const courses = open.slice(0, top);
  if (courses.length === 0) return shell(<StateView kind="empty" title={t("empty")} message="" icon="book" />);
  const today = new Date().toISOString().slice(0, 10);
  return shell(
    <ul className="home-list" aria-label={title}>
      {courses.map((c) => {
        const name = localText(c.titles, locale);
        const date = c.dueOn ? format.dateTime(new Date(`${c.dueOn}T12:00:00Z`), { dateStyle: "medium", timeZone: "UTC" }) : null;
        return (
          <li key={c.assignmentId}>
            <Link href={courseHref(c.courseId)}>
              <bdi>{name}</bdi>
            </Link>
            {date ? (
              <span className="home-meta">
                {c.dueOn! < today ? <Badge tone="danger" icon="clock">{t("overdue", { date })}</Badge> : t("due", { date })}
              </span>
            ) : null}
            <Progress label={t("progressOf", { title: name })} value={c.percent} valueText={t("percent", { value: c.percent })} />
          </li>
        );
      })}
    </ul>,
  );
}
