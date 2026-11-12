"use client";

import Link from "next/link";
import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { Icon } from "@/components/ui/Icon";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { Progress } from "@/components/ui/Status";
import { readHref } from "@/lib/content/types";
import { documentHref } from "@/lib/documents/types";
import {
  builderHref,
  call,
  certificateHref,
  courseLocale,
  learningApi,
  localText,
  send,
  type AttemptResult,
  type LearnerCourse,
  type LearnerLesson,
  type LearnerQuestion,
} from "@/lib/learning";
import { StateBadge, useDue } from "./parts";
import "./learning.css";

const lessonIcons = { page: "news", document: "files", video: "video", quiz: "tasks" } as const;

/**
 * The course player (spec 028 US3): enrol where the course is open, take the lessons in order or in any order, resume at
 * the last lesson on any device, and answer quizzes. Whether the viewer may enrol, how many attempts are left and when
 * the course is complete come from the API.
 */
export function Player({ initial, startPath }: { initial: LearnerCourse; startPath?: string }) {
  const t = useTranslations("learning.player");
  const locale = useLocale();
  const [course, setCourse] = useState(initial);
  const reading = courseLocale(course.locales, locale);
  const firstOpen = course.lessons.find((l) => !l.done)?.key ?? course.lessons[0]?.key ?? null;
  const [current, setCurrent] = useState<string | null>(course.me.lastLessonKey ?? firstOpen);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const due = useDue()(course.me.state === "open" ? course.me.dueOn : null);
  const open = course.me.state === "open";
  const lesson = course.lessons.find((l) => l.key === current) ?? null;
  const title = localText(course.titles, reading);

  async function run<T>(request: () => Promise<Response>, then: (value: T) => void) {
    setBusy(true);
    setMessages([]);
    const result = await call<T>(request, t("failed"));
    if (result.ok) then(result.value);
    else setMessages(result.messages);
    setBusy(false);
  }

  const enrol = () =>
    run<LearnerCourse>(() => send(startPath ?? `${learningApi}/courses/${course.id}:enrol`, "POST"), (view) => {
      setCourse(view);
      setCurrent(view.lessons[0]?.key ?? null);
    });

  function choose(key: string) {
    setCurrent(key);
    if (open) void run<LearnerCourse>(() => send(`${learningApi}/courses/${course.id}/position`, "PUT", { lessonKey: key }), setCourse);
  }

  const complete = (key: string) =>
    run<LearnerCourse>(() => send(`${learningApi}/courses/${course.id}/lessons/${encodeURIComponent(key)}:complete`, "POST"), (view) => {
      setCourse(view);
      const next = view.lessons.find((l) => !l.done);
      if (next) setCurrent(next.key);
    });

  return (
    <div className="learning">
      <section className="learning-section" aria-labelledby="course-about">
        <h2 id="course-about">{t("about")}</h2>
        <p className="learning-meta">
          <bdi>{localText(course.summaries, reading)}</bdi>
        </p>
        <div className="learning-row">
          <StateBadge state={course.me.state} />
          {due ? <Badge tone={due.overdue ? "danger" : "neutral"} icon="clock">{due.text}</Badge> : null}
          {course.minutes ? <Badge icon="clock">{t("minutes", { count: course.minutes })}</Badge> : null}
          {reading !== locale ? <Badge tone="info" icon="globe">{t("otherLanguage")}</Badge> : null}
        </div>
        {course.me.state !== "none" ? (
          <Progress label={t("progressOf", { title })} value={course.me.percent} valueText={t("percent", { value: course.me.percent })} />
        ) : null}
        {course.me.state === "completed" && course.me.certificateId ? (
          <Alert tone="success" title={t("completedTitle")} action={<ButtonLink href={certificateHref(course.me.certificateId)} icon="star">{t("certificate")}</ButtonLink>}>
            {t("completed")}
          </Alert>
        ) : null}
        {course.me.state === "retraining" ? (
          <Alert tone="warning" title={t("retrainingTitle")}>
            {t("retraining")}
          </Alert>
        ) : null}
        {(course.me.state === "none" || course.me.state === "retraining") && (course.canEnrol || startPath) ? (
          <div className="learning-row">
            <Button variant="primary" icon="plus" busy={busy} onClick={() => void enrol()}>
              {t("enrol")}
            </Button>
          </div>
        ) : null}
        {course.me.state === "none" && !course.canEnrol && !startPath ? <p className="learning-meta">{t("notEnrolled")}</p> : null}
        {course.manages ? (
          <div className="learning-row">
            <ButtonLink href={builderHref(course.id)} icon="edit" size="sm">
              {t("manage")}
            </ButtonLink>
          </div>
        ) : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
      </section>
      <div className="learning-player">
        <nav className="learning-section" aria-labelledby="course-outline">
          <h2 id="course-outline">{t("outline")}</h2>
          <ol className="learning-outline">
            {course.lessons.map((l, index) => (
              <li key={l.key}>
                <button type="button" aria-current={l.key === current ? "step" : undefined} onClick={() => choose(l.key)}>
                  <Icon name={l.done ? "check" : lessonIcons[l.kind]} label={l.done ? t("done") : t(`kind.${l.kind}`)} />
                  <bdi>{t("lessonNumber", { number: index + 1, title: localText(l.titles, reading) })}</bdi>
                </button>
              </li>
            ))}
          </ol>
        </nav>
        {lesson ? (
          <Lesson
            key={lesson.key}
            courseId={course.id}
            lesson={lesson}
            reading={reading}
            open={open}
            busy={busy}
            onComplete={() => void complete(lesson.key)}
            onAttempt={(result) => setCourse(result.course)}
          />
        ) : (
          <p className="learning-meta">{t("noLessons")}</p>
        )}
      </div>
    </div>
  );
}

function Lesson({
  courseId,
  lesson,
  reading,
  open,
  busy,
  onComplete,
  onAttempt,
}: {
  courseId: string;
  lesson: LearnerLesson;
  reading: string;
  open: boolean;
  busy: boolean;
  onComplete: () => void;
  onAttempt: (result: AttemptResult) => void;
}) {
  const t = useTranslations("learning.player");
  const source = lesson.sources ? (lesson.sources[reading] ?? Object.values(lesson.sources)[0] ?? null) : null;
  const href = source ? (lesson.kind === "page" ? readHref(source) : documentHref(source)) : null;
  return (
    <section className="learning-section" aria-labelledby="lesson-title">
      <h2 id="lesson-title">
        <bdi>{localText(lesson.titles, reading)}</bdi>
      </h2>
      <div className="learning-row">
        <Badge icon={lessonIcons[lesson.kind]}>{t(`kind.${lesson.kind}`)}</Badge>
        {lesson.minutes ? <Badge icon="clock">{t("minutes", { count: lesson.minutes })}</Badge> : null}
        {lesson.done ? <Badge tone="success" icon="check">{t("done")}</Badge> : null}
      </div>
      {lesson.quiz ? (
        <Quiz courseId={courseId} lesson={lesson} reading={reading} open={open} onAttempt={onAttempt} />
      ) : (
        <>
          <p className="learning-meta">{t(`open.${lesson.kind}`)}</p>
          {href ? (
            <div className="learning-row">
              <Link href={href} target="_blank" rel="noopener">
                {t("openSource")}
              </Link>
            </div>
          ) : null}
          {open && !lesson.done ? (
            <div className="learning-row">
              <Button variant="primary" icon="check" busy={busy} onClick={onComplete}>
                {t("markDone")}
              </Button>
            </div>
          ) : null}
        </>
      )}
    </section>
  );
}

function Quiz({
  courseId,
  lesson,
  reading,
  open,
  onAttempt,
}: {
  courseId: string;
  lesson: LearnerLesson;
  reading: string;
  open: boolean;
  onAttempt: (result: AttemptResult) => void;
}) {
  const t = useTranslations("learning.player");
  const quiz = lesson.quiz!;
  const [answers, setAnswers] = useState<Record<string, string[]>>({});
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [last, setLast] = useState<{ score: number; passed: boolean } | null>(null);
  const canTry = open && !lesson.done && quiz.attemptsLeft > 0;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    const result = await call<AttemptResult>(
      () => send(`${learningApi}/courses/${courseId}/lessons/${encodeURIComponent(lesson.key)}/attempts`, "POST", { answers }),
      t("quiz.failed"),
    );
    if (result.ok) {
      setLast({ score: result.value.score, passed: result.value.passed });
      if (!result.value.passed) setAnswers({});
      onAttempt(result.value);
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  return (
    <form className="learning" onSubmit={(e) => void submit(e)}>
      <p className="learning-meta">
        {t("quiz.rules", { mark: quiz.passMark, attempts: quiz.attempts })}
        {quiz.bestScore !== null ? ` ${t("quiz.best", { score: quiz.bestScore })}` : ""}
      </p>
      {last ? (
        <Alert tone={last.passed ? "success" : "warning"} title={last.passed ? t("quiz.passedTitle") : t("quiz.failedTitle")} live>
          {last.passed ? t("quiz.passed", { score: last.score }) : t("quiz.notPassed", { score: last.score, left: quiz.attemptsLeft })}
        </Alert>
      ) : null}
      {lesson.done ? <p className="learning-meta">{t("quiz.alreadyPassed")}</p> : null}
      {open && !lesson.done && quiz.attemptsLeft === 0 ? (
        <Alert tone="danger" title={t("quiz.noAttemptsTitle")}>
          {t("quiz.noAttempts")}
        </Alert>
      ) : null}
      {canTry ? (
        <>
          {quiz.questions.map((question, index) => (
            <Question
              key={question.key}
              number={index + 1}
              question={question}
              reading={reading}
              value={answers[question.key] ?? []}
              onChange={(value) => setAnswers((a) => ({ ...a, [question.key]: value }))}
            />
          ))}
          {messages.length > 0 ? (
            <Alert tone="danger" title={t("quiz.failed")} live>
              {messages.join(" ")}
            </Alert>
          ) : null}
          <div className="learning-row">
            <Button type="submit" variant="primary" busy={busy}>
              {t("quiz.submit")}
            </Button>
            <span className="learning-meta">{t("quiz.attemptsLeft", { count: quiz.attemptsLeft })}</span>
          </div>
        </>
      ) : null}
    </form>
  );
}

function Question({
  number,
  question,
  reading,
  value,
  onChange,
}: {
  number: number;
  question: LearnerQuestion;
  reading: string;
  value: string[];
  onChange: (value: string[]) => void;
}) {
  const t = useTranslations("learning.player");
  const prompt = t("quiz.question", { number, prompt: localText(question.prompts, reading) });
  const name = `question-${question.key}`;
  if (question.kind === "multiple") {
    return (
      <fieldset className="learning-question">
        <legend>
          <bdi>{prompt}</bdi>
        </legend>
        <p className="learning-meta">{t("quiz.chooseAll")}</p>
        {question.options.map((option) => (
          <Checkbox
            key={option.key}
            name={name}
            label={<bdi>{localText(option.labels, reading)}</bdi>}
            checked={value.includes(option.key)}
            onChange={(e) => onChange(e.target.checked ? [...value, option.key] : value.filter((k) => k !== option.key))}
          />
        ))}
      </fieldset>
    );
  }
  return (
    <RadioGroup
      legend={prompt}
      name={name}
      value={value[0] ?? ""}
      onChange={(e) => onChange([e.target.value])}
      options={question.options.map((option) => ({ value: option.key, label: localText(option.labels, reading) }))}
    />
  );
}
