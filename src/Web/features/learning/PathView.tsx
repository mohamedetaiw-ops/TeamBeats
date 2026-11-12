"use client";

import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { ButtonLink } from "@/components/ui/Button";
import { Progress } from "@/components/ui/Status";
import { StateView } from "@/components/ui/StateView";
import { courseHref, localText, pathBuilderHref, type PathView as Path } from "@/lib/learning";
import { StateBadge } from "./parts";
import "./learning.css";

/**
 * A learning path (spec 028 FR-004): its courses in order. A course that needs the one before it stays locked until that
 * one is complete; the API decides, and refuses to start a locked course however it is asked.
 */
export function PathView({ path }: { path: Path }) {
  const t = useTranslations("learning.path");
  const locale = useLocale();
  const done = path.courses.filter((c) => c.state === "completed").length;
  return (
    <div className="learning">
      <section className="learning-section" aria-labelledby="path-courses">
        <h2 id="path-courses">{t("courses")}</h2>
        <p className="learning-meta">
          <bdi>{localText(path.summaries, locale)}</bdi>
        </p>
        <Progress label={t("progress")} value={path.courses.length === 0 ? 0 : (done * 100) / path.courses.length} valueText={t("progressText", { done, total: path.courses.length })} />
        {path.manages ? (
          <div className="learning-row">
            <ButtonLink href={pathBuilderHref(path.id)} icon="edit" size="sm">
              {t("manage")}
            </ButtonLink>
          </div>
        ) : null}
        {path.courses.length === 0 ? (
          <StateView kind="empty" title={t("emptyTitle")} message={t("empty")} icon="book" />
        ) : (
          <ol className="learning-list" aria-label={t("courses")}>
            {path.courses.map((course, index) => {
              const title = localText(course.titles, locale);
              return (
                <li key={course.courseId}>
                  <div className="learning-row">
                    <span>{t("step", { number: index + 1 })}</span>
                    {course.locked ? (
                      <bdi>{title}</bdi>
                    ) : (
                      <Link href={`${courseHref(course.courseId)}?path=${encodeURIComponent(path.id)}`}>
                        <bdi>{title}</bdi>
                      </Link>
                    )}
                    {course.locked ? (
                      <Badge icon="lock">{t("locked")}</Badge>
                    ) : (
                      <StateBadge state={course.state} />
                    )}
                  </div>
                  {course.locked ? <p className="learning-meta">{t("lockedHint")}</p> : null}
                  {course.state === "open" ? (
                    <Progress label={t("progressOf", { title })} value={course.percent} valueText={t("percent", { value: course.percent })} />
                  ) : null}
                </li>
              );
            })}
          </ol>
        )}
      </section>
    </div>
  );
}
