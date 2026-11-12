"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { Tabs } from "@/components/ui/Tabs";
import { unitLabel } from "@/lib/admin/types";
import { languageName } from "@/lib/format";
import {
  call,
  courseHref,
  learningApi,
  localText,
  send,
  type Audience,
  type CourseDetail,
  type CourseLearner,
  type LessonKind,
  type QuestionKind,
  type Texts,
} from "@/lib/learning";
import { statusTones } from "./ManageHome";
import { toggle, useUnits } from "./parts";
import "./learning.css";

type EditOption = { uid: string; key: string; labels: Texts; correct: boolean };
type EditQuestion = { uid: string; key: string; kind: QuestionKind; prompts: Texts; options: EditOption[] };
type EditQuiz = { passMark: number; attempts: number; questions: EditQuestion[] };
type EditLesson = { uid: string; key: string; kind: LessonKind; titles: Texts; sources: Texts; minutes: string; quiz: EditQuiz | null };

let counter = 0;
const uid = () => `n${++counter}`;
const guidIn = (text: string) => text.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i)?.[0] ?? null;
const only = (texts: Texts, langs: string[]) => Object.fromEntries(langs.filter((l) => (texts[l] ?? "").trim()).map((l) => [l, (texts[l] ?? "").trim()]));

const newOption = (correct = false): EditOption => ({ uid: uid(), key: "", labels: {}, correct });
const newQuestion = (): EditQuestion => ({ uid: uid(), key: "", kind: "single", prompts: {}, options: [newOption(true), newOption()] });
const newLesson = (kind: LessonKind): EditLesson => ({
  uid: uid(),
  key: "",
  kind,
  titles: {},
  sources: {},
  minutes: "",
  quiz: kind === "quiz" ? { passMark: 70, attempts: 3, questions: [newQuestion()] } : null,
});

function editable(detail: CourseDetail): EditLesson[] {
  return detail.draft.lessons.map((l) => ({
    uid: uid(),
    key: l.key,
    kind: l.kind,
    titles: { ...l.titles },
    sources: { ...(l.sources ?? {}) },
    minutes: l.minutes ? String(l.minutes) : "",
    quiz: l.quiz
      ? {
          passMark: l.quiz.passMark,
          attempts: l.quiz.attempts,
          questions: l.quiz.questions.map((q) => ({
            uid: uid(),
            key: q.key,
            kind: q.kind,
            prompts: { ...q.prompts },
            options: q.options.map((o) => ({ uid: uid(), key: o.key, labels: { ...o.labels }, correct: o.correct })),
          })),
        }
      : null,
  }));
}

/**
 * The course builder (spec 028 US1): the course in each of its languages, its lessons (a page, a document, a video or
 * a quiz), who sees it in the catalogue, then publish as a new version, with or without retraining, or retire it. The
 * lessons point at pages and files that stay where they are; the API checks each one can be read before publishing.
 */
export function CourseBuilder({ initial, locales }: { initial: CourseDetail; locales: string[] }) {
  const t = useTranslations("learning.builder");
  return (
    <Tabs
      label={t("tabs")}
      tabs={[
        { key: "course", label: t("courseTab"), icon: "edit", content: <CourseForm initial={initial} locales={locales} /> },
        { key: "learners", label: t("learnersTab"), icon: "people", content: <Learners course={initial} /> },
      ]}
    />
  );
}

function CourseForm({ initial, locales }: { initial: CourseDetail; locales: string[] }) {
  const t = useTranslations("learning.builder");
  const locale = useLocale();
  const units = useUnits();
  const [detail, setDetail] = useState(initial);
  const [langs, setLangs] = useState<string[]>(() => {
    const given = Object.keys(initial.draft.titles);
    return given.length > 0 ? given : [locale];
  });
  const [titles, setTitles] = useState<Texts>(initial.draft.titles);
  const [summaries, setSummaries] = useState<Texts>(initial.draft.summaries);
  const [minutes, setMinutes] = useState(initial.draft.minutes ? String(initial.draft.minutes) : "");
  const [category, setCategory] = useState(initial.category ?? "");
  const [unit, setUnit] = useState(initial.orgUnitId ?? "");
  const [openEnrolment, setOpenEnrolment] = useState(initial.openEnrolment);
  const clause = initial.audience.anyOf?.[0];
  const [audienceUnits, setAudienceUnits] = useState<string[]>(clause?.orgUnits?.map((u) => u.id) ?? []);
  const [subtree, setSubtree] = useState(clause?.orgUnits?.[0]?.subtree ?? true);
  const [lessons, setLessons] = useState<EditLesson[]>(() => editable(initial));
  const [addKind, setAddKind] = useState<LessonKind>("page");
  const [retraining, setRetraining] = useState(false);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const readOnly = detail.status === "retired";

  const change = <T,>(set: (v: T) => void) => (v: T) => {
    set(v);
    setDirty(true);
  };
  const editLesson = (id: string, update: (l: EditLesson) => EditLesson) => {
    setLessons((list) => list.map((l) => (l.uid === id ? update(l) : l)));
    setDirty(true);
  };

  function audience(): Audience {
    return audienceUnits.length === 0 ? { anyOf: [] } : { anyOf: [{ orgUnits: audienceUnits.map((id) => ({ id, subtree })) }] };
  }

  function body() {
    return {
      revision: detail.revision,
      orgUnitId: unit || null,
      category: category.trim() || null,
      audience: audience(),
      openEnrolment,
      definition: {
        titles: only(titles, langs),
        summaries: only(summaries, langs),
        minutes: minutes ? Number(minutes) : null,
        lessons: lessons.map((l) => ({
          key: l.key || null,
          kind: l.kind,
          titles: only(l.titles, langs),
          sources:
            l.kind === "quiz"
              ? null
              : Object.fromEntries(langs.map((g) => [g, guidIn(l.sources[g] ?? "")]).filter((pair): pair is [string, string] => pair[1] !== null)),
          minutes: l.minutes ? Number(l.minutes) : null,
          quiz: l.quiz
            ? {
                passMark: l.quiz.passMark,
                attempts: l.quiz.attempts,
                questions: l.quiz.questions.map((q) => ({
                  key: q.key || null,
                  kind: q.kind,
                  prompts: only(q.prompts, langs),
                  options: q.options.map((o) => ({ key: o.key || null, labels: only(o.labels, langs), correct: o.correct })),
                })),
              }
            : null,
        })),
      },
    };
  }

  function loaded(next: CourseDetail) {
    setDetail(next);
    setLessons(editable(next));
    setTitles(next.draft.titles);
    setSummaries(next.draft.summaries);
    setDirty(false);
  }

  async function run(request: () => Promise<Response>, done: string) {
    setBusy(true);
    setMessages([]);
    setNotice(null);
    const result = await call<CourseDetail>(request, t("failed"));
    if (result.ok) {
      loaded(result.value);
      setNotice(done);
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  const save = () => run(() => send(`${learningApi}/manage/courses/${detail.id}`, "PUT", body()), t("saved"));

  async function publish() {
    if (dirty) {
      setBusy(true);
      const saved = await call<CourseDetail>(() => send(`${learningApi}/manage/courses/${detail.id}`, "PUT", body()), t("failed"));
      setBusy(false);
      if (!saved.ok) {
        setMessages(saved.messages);
        return;
      }
      loaded(saved.value);
      await run(() => send(`${learningApi}/manage/courses/${detail.id}:publish`, "POST", { revision: saved.value.revision, requiresRetraining: retraining }), t("published"));
      return;
    }
    await run(() => send(`${learningApi}/manage/courses/${detail.id}:publish`, "POST", { revision: detail.revision, requiresRetraining: retraining }), t("published"));
  }

  const retire = () => run(() => send(`${learningApi}/manage/courses/${detail.id}:retire`, "POST"), t("retiredNotice"));

  return (
    <form
      className="learning"
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <section className="learning-section" aria-labelledby="builder-state">
        <h2 id="builder-state">{t("state")}</h2>
        <div className="learning-row">
          <Badge tone={statusTones[detail.status]}>{t(`status.${detail.status}`)}</Badge>
          {detail.publishedVersion ? <Badge>{t("version", { version: detail.publishedVersion })}</Badge> : null}
          {detail.draftChanged && detail.publishedVersion ? <Badge tone="info" icon="edit">{t("unpublishedChanges")}</Badge> : null}
          {detail.publishedVersion && detail.status === "published" ? <Link href={courseHref(detail.id)}>{t("openAsLearner")}</Link> : null}
        </div>
        {detail.changedSources.length > 0 ? (
          <Alert tone="warning" title={t("sourcesChangedTitle")}>
            {t("sourcesChanged", { count: detail.changedSources.length })}
          </Alert>
        ) : null}
        {notice ? (
          <Alert tone="success" title={notice} live>
            {dirty ? t("unsaved") : ""}
          </Alert>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
      </section>

      <fieldset className="learning-section" disabled={readOnly}>
        <legend>{t("details")}</legend>
        <fieldset className="learning-question">
          <legend>{t("languages")}</legend>
          <p className="learning-meta">{t("languagesHint")}</p>
          <div className="learning-row">
            {locales.map((l) => (
              <Checkbox
                key={l}
                name="course-language"
                label={languageName(l, locale)}
                checked={langs.includes(l)}
                disabled={langs.length === 1 && langs.includes(l)}
                onChange={(e) => change(setLangs)(toggle(langs, l, e.target.checked))}
              />
            ))}
          </div>
        </fieldset>
        {langs.map((l) => (
          <div key={l} className="learning-row">
            <FormField
              label={t("titleIn", { language: languageName(l, locale) })}
              name={`course-title-${l}`}
              value={titles[l] ?? ""}
              maxLength={200}
              lang={l}
              dir="auto"
              onChange={(e) => change(setTitles)({ ...titles, [l]: e.target.value })}
            />
            <TextAreaField
              label={t("summaryIn", { language: languageName(l, locale) })}
              name={`course-summary-${l}`}
              value={summaries[l] ?? ""}
              maxLength={2000}
              rows={2}
              lang={l}
              dir="auto"
              onChange={(e) => change(setSummaries)({ ...summaries, [l]: e.target.value })}
            />
          </div>
        ))}
        <div className="learning-row">
          <FormField label={t("category")} name="course-category" value={category} maxLength={60} dir="auto" onChange={(e) => change(setCategory)(e.target.value)} />
          <FormField
            label={t("minutes")}
            name="course-minutes"
            type="number"
            min={1}
            max={600}
            value={minutes}
            onChange={(e) => change(setMinutes)(e.target.value)}
          />
          <SelectField
            label={t("owner")}
            name="course-unit"
            value={unit}
            hint={t("ownerHint")}
            onChange={(e) => change(setUnit)(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
          />
        </div>
      </fieldset>

      <fieldset className="learning-section" disabled={readOnly}>
        <legend>{t("audience")}</legend>
        <p className="learning-meta">{t("audienceHint")}</p>
        <Checkbox name="course-open" label={t("openEnrolment")} hint={t("openEnrolmentHint")} checked={openEnrolment} onChange={(e) => change(setOpenEnrolment)(e.target.checked)} />
        {units.length > 0 ? (
          <div className="learning-row">
            {units.map((u) => (
              <Checkbox
                key={u.id}
                name="course-audience"
                label={<bdi>{unitLabel(u, locale)}</bdi>}
                checked={audienceUnits.includes(u.id)}
                onChange={(e) => change(setAudienceUnits)(toggle(audienceUnits, u.id, e.target.checked))}
              />
            ))}
          </div>
        ) : null}
        <Checkbox name="course-subtree" label={t("subtree")} checked={subtree} onChange={(e) => change(setSubtree)(e.target.checked)} />
        <p className="learning-meta">{audienceUnits.length === 0 ? t("everyone") : t("targeted", { count: audienceUnits.length })}</p>
      </fieldset>

      <fieldset className="learning-section" disabled={readOnly}>
        <legend>{t("lessons")}</legend>
        {lessons.length === 0 ? <p className="learning-meta">{t("noLessons")}</p> : null}
        <ol className="learning-list" aria-label={t("lessons")}>
          {lessons.map((lesson, index) => (
            <li key={lesson.uid}>
              <LessonEditor
                lesson={lesson}
                index={index}
                count={lessons.length}
                langs={langs}
                onChange={(update) => editLesson(lesson.uid, update)}
                onMove={(by) => {
                  setLessons((list) => {
                    const next = [...list];
                    const [moved] = next.splice(index, 1);
                    if (moved) next.splice(index + by, 0, moved);
                    return next;
                  });
                  setDirty(true);
                }}
                onRemove={() => {
                  setLessons((list) => list.filter((l) => l.uid !== lesson.uid));
                  setDirty(true);
                }}
              />
            </li>
          ))}
        </ol>
        <div className="learning-row">
          <SelectField
            label={t("lessonKind")}
            name="lesson-kind"
            value={addKind}
            onChange={(e) => setAddKind(e.target.value as LessonKind)}
            options={(["page", "document", "video", "quiz"] as const).map((k) => ({ value: k, label: t(`kind.${k}`) }))}
          />
          <Button
            icon="plus"
            disabled={lessons.length >= 50}
            onClick={() => {
              setLessons((list) => [...list, newLesson(addKind)]);
              setDirty(true);
            }}
          >
            {t("addLesson")}
          </Button>
        </div>
      </fieldset>

      {readOnly ? (
        <p className="learning-meta">{t("retiredHint")}</p>
      ) : (
        <section className="learning-section" aria-labelledby="builder-actions">
          <h2 id="builder-actions">{t("actions")}</h2>
          {detail.publishedVersion ? (
            <Checkbox name="course-retraining" label={t("requiresRetraining")} hint={t("requiresRetrainingHint")} checked={retraining} onChange={(e) => setRetraining(e.target.checked)} />
          ) : null}
          <div className="learning-row">
            <Button type="submit" busy={busy} icon="check">
              {t("save")}
            </Button>
            <Button variant="primary" busy={busy} icon="upload" onClick={() => void publish()}>
              {detail.publishedVersion ? t("publishNew") : t("publish")}
            </Button>
            {detail.status === "published" ? (
              <ConfirmAction label={t("retire")} question={t("retireQuestion")} confirmLabel={t("retireConfirm")} cancelLabel={t("cancel")} onConfirm={retire} />
            ) : null}
          </div>
        </section>
      )}
    </form>
  );
}

function LessonEditor({
  lesson,
  index,
  count,
  langs,
  onChange,
  onMove,
  onRemove,
}: {
  lesson: EditLesson;
  index: number;
  count: number;
  langs: string[];
  onChange: (update: (l: EditLesson) => EditLesson) => void;
  onMove: (by: number) => void;
  onRemove: () => void;
}) {
  const t = useTranslations("learning.builder");
  const locale = useLocale();
  const id = lesson.uid;
  return (
    <div className="learning-builder-lesson">
      <div className="learning-row">
        <h3>{t("lessonNumber", { number: index + 1, kind: t(`kind.${lesson.kind}`) })}</h3>
        <Button size="xs" variant="quiet" disabled={index === 0} onClick={() => onMove(-1)}>
          {t("moveUp")}
        </Button>
        <Button size="xs" variant="quiet" disabled={index === count - 1} onClick={() => onMove(1)}>
          {t("moveDown")}
        </Button>
        <Button size="xs" variant="destructive" icon="trash" onClick={onRemove}>
          {t("removeLesson")}
        </Button>
      </div>
      {langs.map((l) => (
        <div key={l} className="learning-row">
          <FormField
            label={t("lessonTitleIn", { language: languageName(l, locale) })}
            name={`${id}-title-${l}`}
            value={lesson.titles[l] ?? ""}
            maxLength={200}
            lang={l}
            dir="auto"
            onChange={(e) => onChange((x) => ({ ...x, titles: { ...x.titles, [l]: e.target.value } }))}
          />
          {lesson.kind !== "quiz" ? (
            <FormField
              label={t(lesson.kind === "page" ? "pageIn" : "fileIn", { language: languageName(l, locale) })}
              name={`${id}-source-${l}`}
              value={lesson.sources[l] ?? ""}
              maxLength={300}
              dir="ltr"
              hint={t(lesson.kind === "page" ? "pageHint" : "fileHint")}
              error={lesson.sources[l] && !guidIn(lesson.sources[l]) ? t("sourceInvalid") : null}
              onChange={(e) => onChange((x) => ({ ...x, sources: { ...x.sources, [l]: e.target.value } }))}
            />
          ) : null}
        </div>
      ))}
      <FormField
        label={t("lessonMinutes")}
        name={`${id}-minutes`}
        type="number"
        min={1}
        max={600}
        value={lesson.minutes}
        onChange={(e) => onChange((x) => ({ ...x, minutes: e.target.value }))}
      />
      {lesson.quiz ? <QuizEditor quiz={lesson.quiz} id={id} langs={langs} onChange={(quiz) => onChange((x) => ({ ...x, quiz }))} /> : null}
    </div>
  );
}

function QuizEditor({ quiz, id, langs, onChange }: { quiz: EditQuiz; id: string; langs: string[]; onChange: (quiz: EditQuiz) => void }) {
  const t = useTranslations("learning.builder");
  const locale = useLocale();
  const setQuestion = (uidOf: string, update: (q: EditQuestion) => EditQuestion) =>
    onChange({ ...quiz, questions: quiz.questions.map((q) => (q.uid === uidOf ? update(q) : q)) });
  return (
    <div className="learning">
      <div className="learning-row">
        <FormField
          label={t("passMark")}
          name={`${id}-pass`}
          type="number"
          min={1}
          max={100}
          value={String(quiz.passMark)}
          onChange={(e) => onChange({ ...quiz, passMark: Number(e.target.value) })}
        />
        <FormField
          label={t("attempts")}
          name={`${id}-attempts`}
          type="number"
          min={1}
          max={10}
          value={String(quiz.attempts)}
          onChange={(e) => onChange({ ...quiz, attempts: Number(e.target.value) })}
        />
      </div>
      {quiz.questions.map((question, qi) => (
        <fieldset key={question.uid} className="learning-builder-lesson">
          <legend>{t("questionNumber", { number: qi + 1 })}</legend>
          <div className="learning-row">
            <SelectField
              label={t("questionKind")}
              name={`${question.uid}-kind`}
              value={question.kind}
              onChange={(e) => {
                const kind = e.target.value as QuestionKind;
                setQuestion(question.uid, (q) => ({
                  ...q,
                  kind,
                  options: kind === "true_false" ? [newOption(true), newOption()] : q.options,
                }));
              }}
              options={(["single", "multiple", "true_false"] as const).map((k) => ({ value: k, label: t(`questionKinds.${k}`) }))}
            />
            <Button size="xs" variant="destructive" icon="trash" disabled={quiz.questions.length === 1} onClick={() => onChange({ ...quiz, questions: quiz.questions.filter((q) => q.uid !== question.uid) })}>
              {t("removeQuestion")}
            </Button>
          </div>
          {langs.map((l) => (
            <TextAreaField
              key={l}
              label={t("promptIn", { language: languageName(l, locale) })}
              name={`${question.uid}-prompt-${l}`}
              value={question.prompts[l] ?? ""}
              maxLength={500}
              rows={2}
              lang={l}
              dir="auto"
              onChange={(e) => setQuestion(question.uid, (q) => ({ ...q, prompts: { ...q.prompts, [l]: e.target.value } }))}
            />
          ))}
          <ol className="learning-list" aria-label={t("options")}>
            {question.options.map((option, oi) => (
              <li key={option.uid}>
                <div className="learning-row">
                  {langs.map((l) => (
                    <FormField
                      key={l}
                      label={t("optionIn", { number: oi + 1, language: languageName(l, locale) })}
                      name={`${option.uid}-label-${l}`}
                      value={option.labels[l] ?? ""}
                      maxLength={200}
                      lang={l}
                      dir="auto"
                      onChange={(e) =>
                        setQuestion(question.uid, (q) => ({
                          ...q,
                          options: q.options.map((o) => (o.uid === option.uid ? { ...o, labels: { ...o.labels, [l]: e.target.value } } : o)),
                        }))
                      }
                    />
                  ))}
                  <Checkbox
                    name={`${option.uid}-correct`}
                    label={t("correct")}
                    checked={option.correct}
                    onChange={(e) =>
                      setQuestion(question.uid, (q) => ({
                        ...q,
                        options: q.options.map((o) =>
                          o.uid === option.uid ? { ...o, correct: e.target.checked } : q.kind === "multiple" || !e.target.checked ? o : { ...o, correct: false },
                        ),
                      }))
                    }
                  />
                  {question.kind !== "true_false" ? (
                    <Button
                      size="xs"
                      variant="quiet"
                      icon="trash"
                      disabled={question.options.length <= 2}
                      onClick={() => setQuestion(question.uid, (q) => ({ ...q, options: q.options.filter((o) => o.uid !== option.uid) }))}
                    >
                      {t("removeOption")}
                    </Button>
                  ) : null}
                </div>
              </li>
            ))}
          </ol>
          {question.kind !== "true_false" ? (
            <div className="learning-row">
              <Button size="sm" icon="plus" disabled={question.options.length >= 10} onClick={() => setQuestion(question.uid, (q) => ({ ...q, options: [...q.options, newOption()] }))}>
                {t("addOption")}
              </Button>
            </div>
          ) : null}
        </fieldset>
      ))}
      <div className="learning-row">
        <Button size="sm" icon="plus" disabled={quiz.questions.length >= 50} onClick={() => onChange({ ...quiz, questions: [...quiz.questions, newQuestion()] })}>
          {t("addQuestion")}
        </Button>
      </div>
    </div>
  );
}

function Learners({ course }: { course: CourseDetail }) {
  const t = useTranslations("learning.builder");
  const locale = useLocale();
  const format = useFormatter();
  const [learners, setLearners] = useState<CourseLearner[] | "loading" | "failed">("loading");
  const [messages, setMessages] = useState<string[]>([]);
  const quizzes = course.draft.lessons.filter((l) => l.kind === "quiz");

  useEffect(() => {
    let live = true;
    void call<CourseLearner[]>(() => fetch(`${learningApi}/manage/courses/${course.id}/learners`, { cache: "no-store" }), t("learners.failed")).then((r) => {
      if (live) setLearners(r.ok ? r.value : "failed");
    });
    return () => {
      live = false;
    };
  }, [course.id, t]);

  async function grant(assignmentId: string, lessonKey: string) {
    setMessages([]);
    const result = await call<CourseLearner[]>(
      () => send(`${learningApi}/manage/courses/${course.id}/learners/${assignmentId}/attempts`, "POST", { lessonKey, extra: 1 }),
      t("learners.failed"),
    );
    if (result.ok) setLearners(result.value);
    else setMessages(result.messages);
  }

  if (learners === "loading") return <StateView kind="loading" minBlockSize="8rem" />;
  if (learners === "failed") return <StateView kind="error" title={t("learners.failed")} message={t("learners.failedHint")} />;
  if (learners.length === 0) return <StateView kind="empty" title={t("learners.emptyTitle")} message={t("learners.empty")} icon="people" />;
  return (
    <section className="learning-section" aria-labelledby="course-learners">
      <h2 id="course-learners">{t("learners.title", { count: learners.length })}</h2>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("learners.failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <ul className="learning-list" aria-label={t("learners.listLabel")}>
        {learners.map((learner) => (
          <li key={learner.assignmentId}>
            <div className="learning-row">
              <strong>
                <bdi>{learner.learner.displayName}</bdi>
              </strong>
              <Badge tone={learner.status === "completed" ? "success" : "info"}>{t(`learners.status.${learner.status}`)}</Badge>
              <span className="learning-meta">{t("learners.percent", { value: learner.percent })}</span>
              {learner.completedAt ? (
                <span className="learning-meta">
                  {t("learners.completedOn", { date: format.dateTime(new Date(learner.completedAt), { dateStyle: "medium" }), version: learner.completedVersion ?? 0 })}
                </span>
              ) : null}
            </div>
            {learner.quizzes.map((q) => {
              const lesson = quizzes.find((l) => l.key === q.lessonKey);
              return (
                <div key={q.lessonKey} className="learning-row">
                  <span className="learning-meta">
                    <bdi>{lesson ? localText(lesson.titles, locale) : q.lessonKey}</bdi>
                  </span>
                  <span className="learning-meta">{t("learners.attemptsUsed", { used: q.used, allowed: q.allowed })}</span>
                  {q.bestScore !== null ? <span className="learning-meta">{t("learners.best", { score: q.bestScore })}</span> : null}
                  {q.passed ? <Badge tone="success" icon="check">{t("learners.passed")}</Badge> : null}
                  {!q.passed && learner.status === "open" && q.used >= q.allowed ? (
                    <Button size="xs" icon="plus" onClick={() => void grant(learner.assignmentId, q.lessonKey)}>
                      {t("learners.grant")}
                    </Button>
                  ) : null}
                </div>
              );
            })}
          </li>
        ))}
      </ul>
    </section>
  );
}
