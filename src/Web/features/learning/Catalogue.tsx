"use client";

import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { FormField } from "@/components/ui/FormField";
import { Progress } from "@/components/ui/Status";
import { StateView } from "@/components/ui/StateView";
import { catalogueHref, courseHref, localText, type CatalogueCourse } from "@/lib/learning";
import { StateBadge } from "./parts";
import "./learning.css";

/**
 * The course catalogue (spec 028 FR-018): only the published courses whose audience includes the viewer, searchable by
 * title. The search runs on the server, so the page works without scripts.
 */
export function Catalogue({ courses, q }: { courses: CatalogueCourse[]; q: string }) {
  const t = useTranslations("learning.catalogue");
  const locale = useLocale();
  return (
    <div className="learning">
      <form className="learning-row" role="search" method="get" action={catalogueHref}>
        <FormField label={t("search")} name="q" type="search" defaultValue={q} maxLength={200} dir="auto" />
        <Button type="submit" icon="search">
          {t("searchButton")}
        </Button>
      </form>
      {courses.length === 0 ? (
        <StateView kind="empty" title={q ? t("noMatchTitle") : t("emptyTitle")} message={q ? t("noMatch") : t("empty")} icon="book" />
      ) : (
        <ul className="learning-grid" aria-label={t("listLabel")}>
          {courses.map((course) => {
            const title = localText(course.titles, locale);
            return (
              <li key={course.id}>
                <Card
                  title={title}
                  href={courseHref(course.id)}
                  headingLevel={2}
                  eyebrow={course.category ? <bdi>{course.category}</bdi> : undefined}
                  footer={
                    <div className="learning-row">
                      <StateBadge state={course.state} />
                      {course.openEnrolment ? <Badge tone="accent">{t("open")}</Badge> : null}
                    </div>
                  }
                >
                  <p className="learning-meta">
                    <bdi>{localText(course.summaries, locale)}</bdi>
                  </p>
                  <p className="learning-meta">{t("shape", { lessons: course.lessons, minutes: course.minutes ?? 0 })}</p>
                  {course.state === "open" ? (
                    <Progress label={t("progressOf", { title })} value={course.percent} valueText={t("percent", { value: course.percent })} />
                  ) : null}
                </Card>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
