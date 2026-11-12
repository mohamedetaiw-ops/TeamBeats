"use client";

import Link from "next/link";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Progress } from "@/components/ui/Status";
import { StateView } from "@/components/ui/StateView";
import { certificateHref, courseHref, localText, pathHref, type MyCourse, type MyLearning as Mine, type PathSummary } from "@/lib/learning";
import { SourceText, StateBadge, useDue } from "./parts";
import "./learning.css";

/**
 * My learning (spec 028 US3): what the viewer was given or chose, what they are taking and what they finished, with the
 * learning paths open to them. What appears comes from the API, so a course outside their audience never shows.
 */
export function MyLearning({ mine, paths }: { mine: Mine; paths: PathSummary[] }) {
  const t = useTranslations("learning.mine");
  const empty = mine.assigned.length + mine.inProgress.length + mine.completed.length === 0;
  return (
    <div className="learning">
      {empty ? <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="book" /> : null}
      <Group id="learning-assigned" title={t("assigned")} courses={mine.assigned} empty={t("assignedEmpty")} />
      <Group id="learning-progress" title={t("inProgress")} courses={mine.inProgress} empty={t("inProgressEmpty")} />
      <Group id="learning-completed" title={t("completed")} courses={mine.completed} empty={t("completedEmpty")} />
      <Paths paths={paths} />
    </div>
  );
}

function Group({ id, title, courses, empty }: { id: string; title: string; courses: MyCourse[]; empty: string }) {
  return (
    <section className="learning-section" aria-labelledby={id}>
      <h2 id={id}>{title}</h2>
      {courses.length === 0 ? (
        <p className="learning-meta">{empty}</p>
      ) : (
        <ul className="learning-list" aria-label={title}>
          {courses.map((course) => (
            <li key={course.assignmentId}>
              <CourseLine course={course} />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function CourseLine({ course }: { course: MyCourse }) {
  const t = useTranslations("learning.mine");
  const locale = useLocale();
  const format = useFormatter();
  const due = useDue()(course.state === "completed" ? null : course.dueOn);
  const title = localText(course.titles, locale);
  return (
    <>
      <div className="learning-row">
        <Link href={courseHref(course.courseId)}>
          <bdi>{title}</bdi>
        </Link>
        <StateBadge state={course.state} />
        {due ? <Badge tone={due.overdue ? "danger" : "neutral"} icon="clock">{due.text}</Badge> : null}
      </div>
      <p className="learning-meta">
        <SourceText source={course.source} />
        {course.minutes ? ` · ${t("minutes", { count: course.minutes })}` : ""}
        {course.completedAt ? ` · ${t("completedOn", { date: format.dateTime(new Date(course.completedAt), { dateStyle: "medium" }) })}` : ""}
      </p>
      {course.state === "completed" ? (
        course.certificateId ? (
          <Link href={certificateHref(course.certificateId)}>{t("certificate")}</Link>
        ) : null
      ) : (
        <Progress label={t("progressOf", { title })} value={course.percent} valueText={t("percent", { value: course.percent })} />
      )}
    </>
  );
}

function Paths({ paths }: { paths: PathSummary[] }) {
  const t = useTranslations("learning.mine");
  const locale = useLocale();
  return (
    <section className="learning-section" aria-labelledby="learning-paths">
      <h2 id="learning-paths">{t("paths")}</h2>
      {paths.length === 0 ? (
        <p className="learning-meta">{t("pathsEmpty")}</p>
      ) : (
        <ul className="learning-list" aria-label={t("paths")}>
          {paths.map((path) => (
            <li key={path.id}>
              <Link href={pathHref(path.id)}>
                <bdi>{localText(path.titles, locale)}</bdi>
              </Link>
              <p className="learning-meta">{t("pathProgress", { done: path.completed, total: path.courses })}</p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
