"use client";

import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { StateView } from "@/components/ui/StateView";
import { Progress } from "@/components/ui/Status";
import type { OrgUnitView } from "@/lib/admin/types";
import { unitLabel } from "@/lib/admin/types";
import { problemMessages } from "@/lib/notifications/types";
import {
  engagementApi,
  localName,
  send,
  surveyHref,
  type SurveyKind,
  type SurveyList,
  type SurveyNamedResponse,
  type SurveyResults,
  type SurveySettingsView,
  type SurveyView,
} from "@/lib/engagement";
import "./engagement.css";

const kinds: SurveyKind[] = ["single", "multiple", "scale", "text"];
const scale = [1, 2, 3, 4, 5];

type DraftQuestion = { kind: SurveyKind; prompt: string; required: boolean; options: string };

/** A `date` value two weeks from now, in the browser's time. */
function inTwoWeeks(): string {
  const d = new Date(Date.now() + 14 * 86_400_000);
  d.setMinutes(d.getMinutes() - d.getTimezoneOffset(), 0, 0);
  return d.toISOString().slice(0, 10);
}

function dateValue(iso: string | null): string {
  if (!iso) return "";
  const d = new Date(iso);
  d.setMinutes(d.getMinutes() - d.getTimezoneOffset(), 0, 0);
  return d.toISOString().slice(0, 10);
}

/** The end of the chosen day in the browser's time, so a survey runs through its last day. */
function endOfDay(value: string): string | null {
  if (!value) return null;
  const d = new Date(`${value}T23:59:00`);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

async function run<T>(call: () => Promise<Response>, onOk: (value: T) => void, onError: (messages: string[]) => void, failed: string) {
  try {
    const response = await call();
    if (response.ok) onOk((await response.json()) as T);
    else onError(await problemMessages(response));
  } catch {
    onError([failed]);
  }
}

/**
 * The surveys page (spec 023 US3): surveys the viewer is asked to answer, the ones they manage, and for survey
 * authors the builder. Engagement administrators also set the tenant's minimum responses. What each person may do
 * comes from the API.
 */
export function SurveyDirectory({ list, settings }: { list: SurveyList; settings: SurveySettingsView | null }) {
  const t = useTranslations("engagement.surveys");
  const router = useRouter();
  return (
    <div className="engagement">
      <section className="engagement-section" aria-labelledby="surveys-to-answer">
        <h2 id="surveys-to-answer">{t("toAnswer")}</h2>
        {list.toAnswer.length === 0 ? (
          <StateView kind="empty" title={t("noneToAnswerTitle")} message={t("noneToAnswer")} icon="tasks" />
        ) : (
          <SurveyItems items={list.toAnswer} />
        )}
      </section>
      {list.managed.length > 0 ? (
        <section className="engagement-section" aria-labelledby="surveys-managed">
          <h2 id="surveys-managed">{t("managed")}</h2>
          <SurveyItems items={list.managed} />
        </section>
      ) : null}
      {list.canCreate ? (
        <section className="engagement-section" aria-labelledby="surveys-new">
          <h2 id="surveys-new">{t("createTitle")}</h2>
          <p className="engagement-meta">{t("createIntro")}</p>
          <SurveyBuilder defaultThreshold={list.defaultThreshold} onSaved={(view) => router.push(surveyHref(view.id))} />
        </section>
      ) : null}
      {settings ? <ThresholdSettings initial={settings} /> : null}
    </div>
  );
}

function SurveyItems({ items }: { items: SurveyView[] }) {
  const t = useTranslations("engagement.surveys");
  const format = useFormatter();
  const locale = useLocale();
  return (
    <ul className="engagement-feed">
      {items.map((s) => (
        <li key={s.id} className="engagement-post">
          <div className="engagement-post-head">
            <h3 className="engagement-post-author" dir="auto">
              <Link href={surveyHref(s.id)}>{s.title}</Link>
            </h3>
            <StatusBadge status={s.status} />
            {s.anonymous ? <Badge tone="accent">{t("anonymousBadge")}</Badge> : null}
            {s.responded ? <Badge tone="success">{t("respondedBadge")}</Badge> : null}
          </div>
          <p className="engagement-meta">
            <bdi>{localName(s.unitNames, locale)}</bdi>
            {s.closesAt ? ` · ${t(s.status === "closed" ? "closedOn" : "closesOn", { when: format.dateTime(new Date(s.closedAt ?? s.closesAt), { dateStyle: "medium" }) })}` : null}
          </p>
        </li>
      ))}
    </ul>
  );
}

function StatusBadge({ status }: { status: SurveyView["status"] }) {
  const t = useTranslations("engagement.surveys");
  return <Badge tone={status === "open" ? "success" : status === "draft" ? "warning" : "neutral"}>{t(`status.${status}`)}</Badge>;
}

function ThresholdSettings({ initial }: { initial: SurveySettingsView }) {
  const t = useTranslations("engagement.surveys");
  const [current, setCurrent] = useState(initial);
  const [value, setValue] = useState(String(initial.threshold));
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    try {
      const response = await fetch(`${engagementApi}/settings/surveys`, {
        method: "PUT",
        headers: { "content-type": "application/json", "if-match": `"s${current.version}"` },
        body: JSON.stringify({ threshold: Number(value) }),
      });
      if (response.ok) {
        setCurrent((await response.json()) as SurveySettingsView);
        setSaved(true);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="engagement-section" aria-labelledby="surveys-settings">
      <h2 id="surveys-settings">{t("settingsTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void save(e)}>
        <FormField label={t("tenantThreshold")} hint={t("tenantThresholdHint")} name="tenant-threshold" type="number" min={3} max={50} value={value} onChange={(e) => setValue(e.target.value)} />
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={t("settingsSaved")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
            {t("save")}
          </Button>
        </div>
      </form>
    </section>
  );
}

/** The survey builder: audience, anonymity, threshold, end date and the questions. A draft changes until it is published. */
export function SurveyBuilder({ initial, defaultThreshold, onSaved }: { initial?: SurveyView; defaultThreshold: number; onSaved: (view: SurveyView) => void }) {
  const t = useTranslations("engagement.surveys");
  const locale = useLocale();
  const [units, setUnits] = useState<OrgUnitView[]>([]);
  const [unit, setUnit] = useState(initial?.orgUnitId ?? "");
  const [title, setTitle] = useState(initial?.title ?? "");
  const [intro, setIntro] = useState(initial?.intro ?? "");
  const [anonymous, setAnonymous] = useState(initial?.anonymous ?? true);
  const [threshold, setThreshold] = useState(String(initial?.threshold ?? defaultThreshold));
  const [closes, setCloses] = useState(initial ? dateValue(initial.closesAt) : inTwoWeeks());
  const [questions, setQuestions] = useState<DraftQuestion[]>(
    initial?.questions.map((q) => ({ kind: q.kind, prompt: q.prompt, required: q.required, options: q.options.map((o) => o.label).join("\n") })) ?? [
      { kind: "single", prompt: "", required: true, options: "" },
    ],
  );
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/org/units", { cache: "no-store" });
        if (live && response.ok) setUnits(((await response.json()) as OrgUnitView[]).filter((u) => u.status === "active"));
      } catch {
        // Without the unit list the author cannot pick an audience; the API still checks the unit they send.
      }
    })();
    return () => {
      live = false;
    };
  }, []);

  const change = (i: number, patch: Partial<DraftQuestion>) => setQuestions((all) => all.map((q, j) => (j === i ? { ...q, ...patch } : q)));

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setSaved(false);
    const body = {
      orgUnitId: unit || null,
      title,
      intro: intro || null,
      anonymous,
      threshold: anonymous ? Number(threshold) : null,
      closesAt: endOfDay(closes),
      questions: questions.map((q) => ({
        kind: q.kind,
        prompt: q.prompt,
        required: q.required,
        options: q.kind === "single" || q.kind === "multiple" ? q.options.split("\n").filter((o) => o.trim()) : [],
      })),
    };
    await run<SurveyView>(
      () => (initial ? send(`${engagementApi}/surveys/${initial.id}`, "PUT", body) : send(`${engagementApi}/surveys`, "POST", body)),
      (view) => {
        setSaved(true);
        onSaved(view);
      },
      setMessages,
      t("failed"),
    );
    setBusy(false);
  }

  return (
    <form className="engagement" onSubmit={(e) => void save(e)}>
      <SelectField
        label={t("unit")}
        name="survey-unit"
        hint={t("unitHint")}
        value={unit}
        onChange={(e) => setUnit(e.target.value)}
        options={[{ value: "", label: t("chooseUnit") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
      />
      <FormField label={t("titleField")} name="survey-title" value={title} maxLength={200} dir="auto" onChange={(e) => setTitle(e.target.value)} />
      <TextAreaField label={t("intro")} name="survey-intro" value={intro} maxLength={2000} rows={2} dir="auto" onChange={(e) => setIntro(e.target.value)} />
      <Checkbox label={t("anonymous")} hint={t("anonymousHint")} checked={anonymous} onChange={(e) => setAnonymous(e.target.checked)} />
      <div className="engagement-row">
        {anonymous ? (
          <FormField
            label={t("threshold")}
            hint={t("thresholdHint", { min: defaultThreshold })}
            name="survey-threshold"
            type="number"
            min={defaultThreshold}
            max={50}
            value={threshold}
            onChange={(e) => setThreshold(e.target.value)}
          />
        ) : null}
        <FormField label={t("closesAt")} name="survey-closes" type="date" value={closes} onChange={(e) => setCloses(e.target.value)} />
      </div>
      <fieldset className="ui-choice-group">
        <legend>{t("questions")}</legend>
        {questions.map((q, i) => (
          <div key={i} className="engagement-section" role="group" aria-label={t("questionNumber", { number: i + 1 })}>
            <div className="engagement-row">
              <SelectField
                label={t("kind")}
                name={`question-kind-${i}`}
                value={q.kind}
                onChange={(e) => change(i, { kind: e.target.value as SurveyKind })}
                options={kinds.map((k) => ({ value: k, label: t(`kinds.${k}`) }))}
              />
              <FormField label={t("prompt")} name={`question-prompt-${i}`} value={q.prompt} maxLength={300} dir="auto" onChange={(e) => change(i, { prompt: e.target.value })} />
            </div>
            {q.kind === "single" || q.kind === "multiple" ? (
              <TextAreaField label={t("options")} hint={t("optionsHint")} name={`question-options-${i}`} value={q.options} rows={3} dir="auto" onChange={(e) => change(i, { options: e.target.value })} />
            ) : null}
            <div className="engagement-row">
              <Checkbox label={t("required")} checked={q.required} onChange={(e) => change(i, { required: e.target.checked })} />
              {questions.length > 1 ? (
                <Button size="sm" variant="quiet" icon="trash" onClick={() => setQuestions((all) => all.filter((_, j) => j !== i))}>
                  {t("removeQuestion")}
                </Button>
              ) : null}
            </div>
          </div>
        ))}
        <div className="engagement-row">
          <Button size="sm" icon="plus" disabled={questions.length >= 50} onClick={() => setQuestions((all) => [...all, { kind: "single", prompt: "", required: false, options: "" }])}>
            {t("addQuestion")}
          </Button>
        </div>
      </fieldset>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {saved ? <Alert tone="success" title={t("saved")} live /> : null}
      <div className="engagement-row">
        <Button type="submit" variant="primary" icon="check" busy={busy} busyLabel={t("working")}>
          {initial ? t("saveDraft") : t("createDraft")}
        </Button>
      </div>
    </form>
  );
}

/**
 * One survey: the builder while it is a draft, the answer form for people in the audience, and results with the
 * export for those who manage it.
 */
export function SurveyDetail({ initial, defaultThreshold }: { initial: SurveyView; defaultThreshold: number }) {
  const t = useTranslations("engagement.surveys");
  const format = useFormatter();
  const locale = useLocale();
  const [survey, setSurvey] = useState(initial);
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  async function act(kind: "publish" | "close") {
    setBusy(kind);
    setMessages([]);
    await run<SurveyView>(() => send(`${engagementApi}/surveys/${survey.id}:${kind}`, "POST"), setSurvey, setMessages, t("failed"));
    setBusy(null);
  }

  return (
    <div className="engagement">
      <div className="engagement-row">
        <StatusBadge status={survey.status} />
        {survey.anonymous ? <Badge tone="accent">{t("anonymousBadge")}</Badge> : <Badge tone="neutral">{t("namedBadge")}</Badge>}
        <p className="engagement-meta">
          <bdi>{localName(survey.unitNames, locale)}</bdi>
          {" · "}
          <bdi>{survey.author.displayName}</bdi>
          {survey.closesAt ? ` · ${t(survey.status === "closed" ? "closedOn" : "closesOn", { when: format.dateTime(new Date(survey.closedAt ?? survey.closesAt), { dateStyle: "medium" }) })}` : null}
        </p>
      </div>
      {survey.intro ? (
        <p dir="auto" className="engagement-meta">
          {survey.intro}
        </p>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {survey.canEdit ? (
        <section className="engagement-section" aria-labelledby="survey-edit">
          <h2 id="survey-edit">{t("editTitle")}</h2>
          <SurveyBuilder initial={survey} defaultThreshold={defaultThreshold} onSaved={setSurvey} />
          <div className="engagement-row">
            <Button variant="primary" icon="megaphone" busy={busy === "publish"} busyLabel={t("working")} onClick={() => void act("publish")}>
              {t("publish")}
            </Button>
          </div>
          <p className="engagement-meta">{t("publishHint")}</p>
        </section>
      ) : null}
      {survey.canRespond ? <SurveyAnswer survey={survey} onAnswered={setSurvey} /> : null}
      {survey.responded ? <Alert tone="success" title={t("thanks")}>{survey.anonymous ? t("thanksAnonymous") : t("thanksNamed")}</Alert> : null}
      {!survey.canRespond && !survey.responded && !survey.manages ? <StateView kind="empty" title={t("notOpenTitle")} message={t("notOpen")} icon="tasks" /> : null}
      {survey.manages && survey.status !== "draft" ? (
        <>
          <div className="engagement-row">
            {survey.status === "open" ? (
              <Button variant="secondary" busy={busy === "close"} busyLabel={t("working")} onClick={() => void act("close")}>
                {t("close")}
              </Button>
            ) : null}
            <ButtonLink variant="secondary" icon="download" href={`${engagementApi}/surveys/${survey.id}/results.csv`}>
              {t("export")}
            </ButtonLink>
          </div>
          <SurveyResultsPanel survey={survey} />
          {!survey.anonymous ? <NamedResponses survey={survey} /> : null}
        </>
      ) : null}
    </div>
  );
}

function SurveyAnswer({ survey, onAnswered }: { survey: SurveyView; onAnswered: (view: SurveyView) => void }) {
  const t = useTranslations("engagement.surveys");
  const [choices, setChoices] = useState<Record<string, string[]>>({});
  const [values, setValues] = useState<Record<string, string>>({});
  const [texts, setTexts] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    type Answer = { questionId: string; optionIds?: string[]; value?: number; text?: string };
    const answers = survey.questions.flatMap((q): Answer[] => {
      if (q.kind === "single" || q.kind === "multiple") return (choices[q.id] ?? []).length > 0 ? [{ questionId: q.id, optionIds: choices[q.id] }] : [];
      if (q.kind === "scale") return values[q.id] ? [{ questionId: q.id, value: Number(values[q.id]) }] : [];
      return (texts[q.id] ?? "").trim() ? [{ questionId: q.id, text: texts[q.id] }] : [];
    });
    await run<SurveyView>(() => send(`${engagementApi}/surveys/${survey.id}/responses`, "POST", { answers }), onAnswered, setMessages, t("failed"));
    setBusy(false);
  }

  return (
    <section className="engagement-section" aria-labelledby="survey-answer">
      <h2 id="survey-answer">{t("answerTitle")}</h2>
      <p className="engagement-meta">{survey.anonymous ? t("answerAnonymous") : t("answerNamed")}</p>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        {survey.questions.map((q) => {
          const legend = q.required ? t("requiredPrompt", { prompt: q.prompt }) : q.prompt;
          if (q.kind === "single") {
            return (
              <RadioGroup
                key={q.id}
                legend={legend}
                name={`answer-${q.id}`}
                value={choices[q.id]?.[0] ?? ""}
                onChange={(e) => setChoices((all) => ({ ...all, [q.id]: [e.target.value] }))}
                options={q.options.map((o) => ({ value: o.id, label: o.label }))}
              />
            );
          }
          if (q.kind === "multiple") {
            return (
              <fieldset key={q.id} className="ui-choice-group">
                <legend dir="auto">{legend}</legend>
                {q.options.map((o) => (
                  <Checkbox
                    key={o.id}
                    label={o.label}
                    checked={(choices[q.id] ?? []).includes(o.id)}
                    onChange={(e) =>
                      setChoices((all) => ({ ...all, [q.id]: e.target.checked ? [...(all[q.id] ?? []), o.id] : (all[q.id] ?? []).filter((x) => x !== o.id) }))
                    }
                  />
                ))}
              </fieldset>
            );
          }
          if (q.kind === "scale") {
            return (
              <RadioGroup
                key={q.id}
                legend={legend}
                name={`answer-${q.id}`}
                value={values[q.id] ?? ""}
                onChange={(e) => setValues((all) => ({ ...all, [q.id]: e.target.value }))}
                options={scale.map((v) => ({ value: String(v), label: t("scaleValue", { value: v }) }))}
              />
            );
          }
          return (
            <TextAreaField key={q.id} label={legend} name={`answer-${q.id}`} value={texts[q.id] ?? ""} maxLength={2000} rows={3} dir="auto"
              onChange={(e) => setTexts((all) => ({ ...all, [q.id]: e.target.value }))} />
          );
        })}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" icon="check" busy={busy} busyLabel={t("working")}>
            {t("submit")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function SurveyResultsPanel({ survey }: { survey: SurveyView }) {
  const t = useTranslations("engagement.surveys");
  const locale = useLocale();
  const [group, setGroup] = useState("");
  const [results, setResults] = useState<SurveyResults | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`${engagementApi}/surveys/${survey.id}/results${group ? `?group=${encodeURIComponent(group)}` : ""}`, { cache: "no-store" });
        if (!live) return;
        if (response.ok) {
          setResults((await response.json()) as SurveyResults);
          setFailed(false);
        } else {
          setFailed(true);
        }
      } catch {
        if (live) setFailed(true);
      }
    })();
    return () => {
      live = false;
    };
  }, [survey.id, survey.status, group]);

  if (failed) return <StateView kind="error" title={t("resultsFailedTitle")} message={t("resultsFailed")} />;
  if (!results) return null;
  const hiddenMessage = results.anonymous && results.status !== "closed" ? t("hiddenUntilClose") : t("hiddenThreshold", { threshold: results.threshold });
  return (
    <section className="engagement-section" aria-labelledby="survey-results">
      <h2 id="survey-results">{t("resultsTitle")}</h2>
      <p className="engagement-meta">{t("responses", { count: results.responses })}</p>
      {results.groups.length > 1 ? (
        <SelectField
          label={t("group")}
          name="survey-group"
          value={group}
          onChange={(e) => setGroup(e.target.value)}
          options={[
            { value: "", label: t("allGroups") },
            ...results.groups.map((g) => ({ value: g.unitId, label: g.visible ? localName(g.names, locale) : t("groupHidden", { name: localName(g.names, locale) }) })),
          ]}
        />
      ) : null}
      {!results.visible ? (
        <Alert tone="info" title={t("hiddenTitle")}>
          {hiddenMessage}
        </Alert>
      ) : (
        results.questions.map((q) => (
          <div key={q.id} className="engagement" role="group" aria-label={q.prompt}>
            <h3 dir="auto">{q.prompt}</h3>
            <p className="engagement-meta">{t("answered", { count: q.answered })}</p>
            {q.options.map((o) => (
              <Progress key={o.id} label={o.label} value={o.count} max={Math.max(q.answered, 1)} valueText={t("count", { count: o.count })} />
            ))}
            {q.scale
              ? q.scale.map((count, i) => (
                  <Progress key={i} label={t("scaleValue", { value: i + 1 })} value={count} max={Math.max(q.answered, 1)} valueText={t("count", { count })} />
                ))
              : null}
            {q.average !== null ? <p className="engagement-meta">{t("average", { value: q.average })}</p> : null}
            {q.texts ? (
              <ul className="engagement-feed" aria-label={t("textAnswers")}>
                {q.texts.map((text, i) => (
                  <li key={i} dir="auto" className="engagement-meta">
                    {text}
                  </li>
                ))}
              </ul>
            ) : null}
          </div>
        ))
      )}
    </section>
  );
}

function NamedResponses({ survey }: { survey: SurveyView }) {
  const t = useTranslations("engagement.surveys");
  const [list, setList] = useState<SurveyNamedResponse[]>([]);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(`${engagementApi}/surveys/${survey.id}/responses`, { cache: "no-store" });
        if (live && response.ok) setList((await response.json()) as SurveyNamedResponse[]);
      } catch {
        // The aggregate above still shows; the named list is extra.
      }
    })();
    return () => {
      live = false;
    };
  }, [survey.id, survey.status]);

  const describe = (a: SurveyNamedResponse["answers"][number]) => {
    const q = survey.questions.find((x) => x.id === a.questionId);
    if (!q) return "";
    const answer = a.text ?? (a.value !== null ? String(a.value) : a.optionIds.map((id) => q.options.find((o) => o.id === id)?.label ?? "").join(", "));
    return t("namedAnswer", { prompt: q.prompt, answer });
  };

  if (list.length === 0) return null;
  return (
    <section className="engagement-section" aria-labelledby="survey-named">
      <h2 id="survey-named">{t("namedTitle")}</h2>
      <ul className="engagement-feed">
        {list.map((r) => (
          <li key={r.person.id} className="engagement-post">
            <h3 className="engagement-post-author">
              <bdi>{r.person.displayName}</bdi>
            </h3>
            {r.answers.map((a) => (
              <p key={a.questionId} className="engagement-meta" dir="auto">
                {describe(a)}
              </p>
            ))}
          </li>
        ))}
      </ul>
    </section>
  );
}
