"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import {
  builderHref,
  call,
  learningApi,
  localText,
  pathBuilderHref,
  send,
  type CourseDetail,
  type CourseSummary,
  type PathSummary,
  type PathView,
} from "@/lib/learning";
import "./learning.css";

export const statusTones: Record<string, BadgeTone> = { draft: "neutral", published: "success", retired: "warning" };

/**
 * Building learning (spec 028 US1): the courses and paths the viewer manages, and a start for a new one. The list holds
 * only what the API says the viewer may manage, in their units.
 */
export function ManageHome({ courses, paths }: { courses: CourseSummary[]; paths: PathSummary[] }) {
  const t = useTranslations("learning.manage");
  const locale = useLocale();
  const format = useFormatter();
  return (
    <div className="learning">
      <NewCourse />
      <section className="learning-section" aria-labelledby="manage-courses">
        <h2 id="manage-courses">{t("courses")}</h2>
        {courses.length === 0 ? (
          <StateView kind="empty" title={t("coursesEmptyTitle")} message={t("coursesEmpty")} icon="book" />
        ) : (
          <ul className="learning-list" aria-label={t("courses")}>
            {courses.map((course) => (
              <li key={course.id}>
                <div className="learning-row">
                  <Link href={builderHref(course.id)}>
                    <bdi>{localText(course.titles, locale)}</bdi>
                  </Link>
                  <Badge tone={statusTones[course.status]}>{t(`status.${course.status}`)}</Badge>
                  {course.publishedVersion ? <Badge>{t("version", { version: course.publishedVersion })}</Badge> : null}
                  {course.draftChanged && course.status !== "draft" ? <Badge tone="info" icon="edit">{t("unpublishedChanges")}</Badge> : null}
                </div>
                <p className="learning-meta">
                  {t("courseMeta", { lessons: course.lessons, date: format.dateTime(new Date(course.updatedAt), { dateStyle: "medium" }) })}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>
      <NewPath courses={courses.filter((c) => c.status !== "retired")} />
      <section className="learning-section" aria-labelledby="manage-paths">
        <h2 id="manage-paths">{t("paths")}</h2>
        {paths.length === 0 ? (
          <p className="learning-meta">{t("pathsEmpty")}</p>
        ) : (
          <ul className="learning-list" aria-label={t("paths")}>
            {paths.map((path) => (
              <li key={path.id}>
                <div className="learning-row">
                  <Link href={pathBuilderHref(path.id)}>
                    <bdi>{localText(path.titles, locale)}</bdi>
                  </Link>
                  <Badge tone={statusTones[path.status]}>{t(`status.${path.status}`)}</Badge>
                </div>
                <p className="learning-meta">{t("pathMeta", { count: path.courses })}</p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function NewCourse() {
  const t = useTranslations("learning.manage");
  const locale = useLocale();
  const router = useRouter();
  const [title, setTitle] = useState("");
  const [category, setCategory] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    const result = await call<CourseDetail>(
      () =>
        send(`${learningApi}/manage/courses`, "POST", {
          category: category || null,
          openEnrolment: false,
          audience: { anyOf: [] },
          definition: { titles: { [locale]: title }, summaries: {}, lessons: [] },
        }),
      t("failed"),
    );
    if (result.ok) router.push(builderHref(result.value.id));
    else {
      setMessages(result.messages);
      setBusy(false);
    }
  }

  return (
    <section className="learning-section" aria-labelledby="manage-new-course">
      <h2 id="manage-new-course">{t("newCourse")}</h2>
      <form className="learning" onSubmit={(e) => void submit(e)}>
        <div className="learning-row">
          <FormField label={t("courseTitle")} name="course-title" value={title} maxLength={200} required dir="auto" onChange={(e) => setTitle(e.target.value)} />
          <FormField label={t("category")} name="course-category" value={category} maxLength={60} dir="auto" onChange={(e) => setCategory(e.target.value)} />
        </div>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <div className="learning-row">
          <Button type="submit" variant="primary" icon="plus" busy={busy}>
            {t("createCourse")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function NewPath({ courses }: { courses: CourseSummary[] }) {
  const t = useTranslations("learning.manage");
  const locale = useLocale();
  const router = useRouter();
  const [title, setTitle] = useState("");
  const [first, setFirst] = useState(courses[0]?.id ?? "");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    const result = await call<PathView>(
      () => send(`${learningApi}/manage/paths`, "POST", { titles: { [locale]: title }, summaries: {}, audience: { anyOf: [] }, courses: [{ courseId: first, requiresPrevious: false }] }),
      t("failed"),
    );
    if (result.ok) router.push(pathBuilderHref(result.value.id));
    else {
      setMessages(result.messages);
      setBusy(false);
    }
  }

  return (
    <section className="learning-section" aria-labelledby="manage-new-path">
      <h2 id="manage-new-path">{t("newPath")}</h2>
      {courses.length === 0 ? <p className="learning-meta">{t("pathNeedsCourse")}</p> : null}
      <form className="learning" onSubmit={(e) => void submit(e)}>
        <div className="learning-row">
          <FormField label={t("pathTitle")} name="path-title" value={title} maxLength={200} required dir="auto" onChange={(e) => setTitle(e.target.value)} />
          <SelectField
            label={t("firstCourse")}
            name="path-first"
            value={first}
            onChange={(e) => setFirst(e.target.value)}
            options={courses.map((c) => ({ value: c.id, label: localText(c.titles, locale) }))}
          />
        </div>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <div className="learning-row">
          <Button type="submit" icon="plus" busy={busy} disabled={!first}>
            {t("createPath")}
          </Button>
        </div>
      </form>
    </section>
  );
}
