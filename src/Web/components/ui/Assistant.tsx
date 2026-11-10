"use client";

import { useId, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode, type RefObject } from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Button } from "./Button";
import { FormField, SelectField, TextAreaField } from "./FormField";
import { Icon, type IconName } from "./Icon";
import { IconButton } from "./IconButton";
import { RadioGroup, type RadioOption } from "./RadioGroup";
import { Meter } from "./Status";

/**
 * Thinking indicator (spec 036 DLS-039): says in words that the assistant is working, with what it is doing now when
 * the feature knows. The dots move only when motion is allowed; the words carry the meaning.
 */
export function ThinkingIndicator({ label, step }: { label?: string; step?: string }) {
  const t = useTranslations("ui.assistant");
  return (
    <div className="ui-thinking" role="status">
      <span className="ui-thinking-dots" aria-hidden="true">
        <span />
        <span />
        <span />
      </span>
      <span className="ui-thinking-text">
        <span>{label ?? t("thinking")}</span>
        {step ? <span className="ui-thinking-step">{step}</span> : null}
      </span>
    </div>
  );
}

export type Attachment = { key: string; name: string; detail?: string; icon?: IconName };

/**
 * Composer (spec 036 DLS-039): the message box of the assistant and of agents. Enter sends and Shift+Enter starts a
 * new line; the box grows with the text. Attached files show as chips that can be removed. While an answer is coming,
 * the send button becomes Stop. Limits and file rules come from the API; the composer shows `notice`.
 */
export function Composer({
  label,
  value,
  onValueChange,
  onSend,
  placeholder,
  attachments = [],
  onAttach,
  onRemove,
  accept,
  busy = false,
  onStop,
  notice,
  disabled = false,
}: {
  label: string;
  value: string;
  onValueChange: (value: string) => void;
  onSend: (value: string) => void;
  placeholder?: string;
  attachments?: readonly Attachment[];
  onAttach?: (files: File[]) => void;
  onRemove?: (key: string) => void;
  accept?: string;
  busy?: boolean;
  onStop?: () => void;
  /** A line under the box, such as how many questions are left today. */
  notice?: ReactNode;
  disabled?: boolean;
}) {
  const t = useTranslations("ui.assistant");
  const id = useId();
  const files = useRef<HTMLInputElement>(null);
  const empty = value.trim() === "" && attachments.length === 0;

  function send(event?: FormEvent) {
    event?.preventDefault();
    if (busy || disabled || empty) return;
    onSend(value);
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      send();
    }
  }

  return (
    <form className="ui-composer" onSubmit={send} data-busy={busy || undefined}>
      <label htmlFor={`${id}-box`} className="visually-hidden">
        {label}
      </label>
      {attachments.length ? (
        <ul className="ui-composer-files" aria-label={t("attached")}>
          {attachments.map((a) => (
            <li key={a.key} className="ui-composer-file">
              <Icon name={a.icon ?? "files"} size={16} />
              <span className="ui-composer-file-name">
                <bdi>{a.name}</bdi>
                {a.detail ? <span>{a.detail}</span> : null}
              </span>
              {onRemove ? <IconButton icon="close" size="xs" label={t("remove", { name: a.name })} onClick={() => onRemove(a.key)} /> : null}
            </li>
          ))}
        </ul>
      ) : null}
      <textarea
        id={`${id}-box`}
        rows={1}
        value={value}
        placeholder={placeholder}
        disabled={disabled}
        aria-describedby={`${id}-keys${notice ? ` ${id}-notice` : ""}`}
        onChange={(e) => onValueChange(e.currentTarget.value)}
        onKeyDown={onKeyDown}
      />
      <div className="ui-composer-bar">
        {onAttach ? (
          <>
            <IconButton icon="attach" size="sm" label={t("attach")} disabled={disabled} onClick={() => files.current?.click()} />
            <input
              ref={files}
              type="file"
              multiple
              accept={accept}
              hidden
              tabIndex={-1}
              onChange={(e) => {
                const chosen = [...(e.currentTarget.files ?? [])];
                if (chosen.length) onAttach(chosen);
                e.currentTarget.value = "";
              }}
            />
          </>
        ) : null}
        <span id={`${id}-keys`} className="ui-composer-keys">
          {t("keys")}
        </span>
        {busy && onStop ? (
          <IconButton icon="stop" size="sm" variant="secondary" label={t("stop")} onClick={onStop} />
        ) : (
          <IconButton type="submit" icon="arrow-up" size="sm" variant="primary" label={t("send")} disabled={disabled || busy || empty} />
        )}
      </div>
      {notice ? (
        <p id={`${id}-notice`} className="ui-composer-notice">
          {notice}
        </p>
      ) : null}
    </form>
  );
}

export type Source = { key: string; title: string; href: string; origin: string; excerpt?: string; icon?: IconName };

/**
 * Source list (spec 036 DLS-039): the numbered sources an answer used, in the order the answer cites them, each with
 * where it came from. The numbers in the answer link here.
 */
export function SourceList({ label, sources, idPrefix }: { label: string; sources: readonly Source[]; idPrefix?: string }) {
  const id = useId();
  const prefix = idPrefix ?? id;
  return (
    <section className="ui-sources" aria-labelledby={`${prefix}-title`}>
      <h4 id={`${prefix}-title`}>{label}</h4>
      <ol>
        {sources.map((s, i) => (
          <li key={s.key} id={`${prefix}-${i + 1}`} className="ui-source">
            <span className="ui-source-number" aria-hidden="true">
              {i + 1}
            </span>
            <span className="ui-source-text">
              <Link href={s.href}>
                <bdi>{s.title}</bdi>
              </Link>
              <span className="ui-source-origin">
                {s.icon ? <Icon name={s.icon} size={14} /> : null}
                <span>{s.origin}</span>
              </span>
              {s.excerpt ? <q className="ui-source-excerpt">{s.excerpt}</q> : null}
            </span>
          </li>
        ))}
      </ol>
    </section>
  );
}

/** A citation mark inside an answer that links to its entry in the source list. */
export function Cite({ number, idPrefix, title }: { number: number; idPrefix: string; title: string }) {
  const t = useTranslations("ui.assistant");
  return (
    <a className="ui-cite" href={`#${idPrefix}-${number}`} aria-label={t("source", { number: String(number), title })}>
      {number}
    </a>
  );
}

/**
 * Usage limit (spec 036 DLS-039): how much of a quota is used, such as questions today or agent runs this month, with
 * when it resets. The tone turns to warning from 80 percent and to danger at the limit; the text says the same.
 */
export function UsageLimit({ label, used, limit, valueText, resetText }: { label: string; used: number; limit: number; valueText: string; resetText?: string }) {
  const share = limit > 0 ? used / limit : 1;
  return (
    <div className="ui-usage">
      <Meter label={label} value={Math.min(used, limit)} max={limit} valueText={valueText} tone={share >= 1 ? "danger" : share >= 0.8 ? "warning" : "accent"} />
      {resetText ? <p className="ui-usage-reset">{resetText}</p> : null}
    </div>
  );
}

export type Question = { key: string; question: string; hint?: string; options: readonly RadioOption[] };

/**
 * Question steps (spec 036 DLS-039): when the assistant or an agent needs a few answers before it can go on, it asks
 * them one at a time, says which step this is, lets the person go back, and shows the answers before sending.
 */
export function QuestionSteps({
  title,
  questions,
  onDone,
  summaryTitle,
}: {
  title: string;
  questions: readonly Question[];
  onDone: (answers: Record<string, string>) => void;
  summaryTitle: string;
}) {
  const t = useTranslations("ui.assistant");
  const id = useId();
  const [step, setStep] = useState(0);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const heading = useRef<HTMLElement>(null);
  const total = questions.length;
  const current = questions[step];
  const review = step >= total;

  function go(next: number) {
    setStep(next);
    // The new question is announced by moving focus to its heading.
    requestAnimationFrame(() => heading.current?.focus());
  }

  return (
    <section className="ui-steps" aria-labelledby={`${id}-title`}>
      <header className="ui-steps-head">
        <h3 id={`${id}-title`}>{title}</h3>
        <ol className="ui-steps-track" aria-label={t("progress")}>
          {questions.map((q, i) => (
            <li key={q.key} aria-current={i === step ? "step" : undefined} data-done={i < step || undefined}>
              <span className="visually-hidden">{t(i < step ? "stepDone" : "stepNumber", { current: String(i + 1), total: String(total) })}</span>
            </li>
          ))}
        </ol>
      </header>
      {review ? (
        <div className="ui-steps-body">
          <h4 ref={heading as RefObject<HTMLHeadingElement>} tabIndex={-1}>
            {summaryTitle}
          </h4>
          <dl className="ui-steps-summary">
            {questions.map((q) => (
              <div key={q.key}>
                <dt>{q.question}</dt>
                <dd>{q.options.find((o) => o.value === answers[q.key])?.label}</dd>
              </div>
            ))}
          </dl>
          <div className="ui-actions">
            <Button onClick={() => go(total - 1)}>{t("back")}</Button>
            <Button variant="primary" icon="check" onClick={() => onDone(answers)}>
              {t("send")}
            </Button>
          </div>
        </div>
      ) : current ? (
        <form
          className="ui-steps-body"
          onSubmit={(e) => {
            e.preventDefault();
            if (answers[current.key]) go(step + 1);
          }}
        >
          <p ref={heading as RefObject<HTMLParagraphElement>} tabIndex={-1} className="ui-steps-count">
            {t("stepNumber", { current: String(step + 1), total: String(total) })}
          </p>
          <RadioGroup
            key={current.key}
            legend={current.question}
            hint={current.hint}
            name={`${id}-${current.key}`}
            options={current.options}
            cards
            value={answers[current.key] ?? ""}
            onChange={(e) => {
              const value = e.currentTarget.value;
              setAnswers((all) => ({ ...all, [current.key]: value }));
            }}
          />
          <div className="ui-actions">
            {step > 0 ? <Button onClick={() => go(step - 1)}>{t("back")}</Button> : null}
            <Button type="submit" variant="primary" iconEnd="arrow-forward" disabled={!answers[current.key]}>
              {step === total - 1 ? t("review") : t("next")}
            </Button>
          </div>
        </form>
      ) : null}
    </section>
  );
}

export type AgentInput =
  | { key: string; label: string; hint?: string; required?: boolean; kind: "text" | "date" | "number" }
  | { key: string; label: string; hint?: string; required?: boolean; kind: "long" }
  | { key: string; label: string; hint?: string; required?: boolean; kind: "choice"; options: { value: string; label: string }[] };

/**
 * Agent form (spec 036 DLS-039): the inputs an agent asks for before a run, drawn from the agent's published input
 * schema, with the API's errors beside each field. The form holds no rule of its own.
 */
export function AgentForm({
  title,
  intro,
  inputs,
  errors = {},
  submitLabel,
  busy = false,
  onSubmit,
}: {
  title: string;
  intro?: string;
  inputs: readonly AgentInput[];
  errors?: Record<string, string>;
  submitLabel: string;
  busy?: boolean;
  onSubmit: (values: Record<string, string>) => void;
}) {
  const t = useTranslations("ui.assistant");
  const id = useId();
  return (
    <form
      className="ui-agent-form"
      aria-labelledby={`${id}-title`}
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        const data = new FormData(e.currentTarget);
        onSubmit(Object.fromEntries(inputs.map((i) => [i.key, String(data.get(i.key) ?? "")])));
      }}
    >
      <h3 id={`${id}-title`}>{title}</h3>
      {intro ? <p className="notice">{intro}</p> : null}
      {inputs.map((input) => {
        const common = { name: input.key, required: input.required, hint: input.hint, error: errors[input.key] ?? null };
        const label = input.required ? input.label : t("optional", { label: input.label });
        if (input.kind === "long") return <TextAreaField key={input.key} label={label} rows={3} {...common} />;
        if (input.kind === "choice")
          return <SelectField key={input.key} label={label} options={[{ value: "", label: t("choose") }, ...input.options]} {...common} />;
        return <FormField key={input.key} label={label} type={input.kind} {...common} />;
      })}
      <div className="ui-actions">
        <Button type="submit" variant="primary" icon="spark" busy={busy} busyLabel={t("starting")}>
          {submitLabel}
        </Button>
      </div>
    </form>
  );
}

export type RunStepStatus = "done" | "running" | "waiting" | "failed" | "skipped";
export type RunStep = { key: string; label: string; status: RunStepStatus; detail?: string; duration?: string };

const statusIcon: Record<RunStepStatus, IconName> = { done: "check", running: "clock", waiting: "minus", failed: "alert", skipped: "minus" };

/**
 * Run progress (spec 036 DLS-039): the steps of an agent run with each one's state in words, the step in progress
 * marked, and failures explained. The current step is announced when it changes.
 */
export function RunProgress({ title, steps, summary }: { title: string; steps: readonly RunStep[]; summary?: ReactNode }) {
  const t = useTranslations("ui.assistant");
  const id = useId();
  const running = steps.find((s) => s.status === "running");
  return (
    <section className="ui-run" aria-labelledby={`${id}-title`}>
      <h3 id={`${id}-title`}>{title}</h3>
      <ol className="ui-run-steps">
        {steps.map((s) => (
          <li key={s.key} className="ui-run-step" data-status={s.status} aria-current={s.status === "running" ? "step" : undefined}>
            <span className="ui-run-mark">
              <Icon name={statusIcon[s.status]} size={14} />
            </span>
            <span className="ui-run-text">
              <span className="ui-run-label">{s.label}</span>
              <span className="ui-run-meta">
                <span>{t(`status.${s.status}`)}</span>
                {s.duration ? <span>{s.duration}</span> : null}
              </span>
              {s.detail ? <span className="ui-run-detail">{s.detail}</span> : null}
            </span>
          </li>
        ))}
      </ol>
      <p className="visually-hidden" role="status">
        {running ? t("now", { step: running.label }) : ""}
      </p>
      {summary ? <div className="ui-run-summary">{summary}</div> : null}
    </section>
  );
}
