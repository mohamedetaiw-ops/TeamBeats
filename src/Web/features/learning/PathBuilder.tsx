"use client";

import Link from "next/link";
import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { unitLabel } from "@/lib/admin/types";
import { languageName } from "@/lib/format";
import { call, learningApi, localText, pathHref, send, type CourseSummary, type PathView, type Texts } from "@/lib/learning";
import { statusTones } from "./ManageHome";
import { toggle, useUnits } from "./parts";
import "./learning.css";

const only = (texts: Texts, langs: string[]) => Object.fromEntries(langs.filter((l) => (texts[l] ?? "").trim()).map((l) => [l, (texts[l] ?? "").trim()]));

/**
 * The path builder (spec 028 FR-004): a path's courses in order, each optionally needing the one before it, and who sees
 * the path. Publishing needs every course in it published; the API checks.
 */
export function PathBuilder({ initial, courses, locales }: { initial: PathView; courses: CourseSummary[]; locales: string[] }) {
  const t = useTranslations("learning.pathBuilder");
  const locale = useLocale();
  const units = useUnits();
  const [path, setPath] = useState(initial);
  const [langs, setLangs] = useState<string[]>(() => {
    const given = Object.keys(initial.titles);
    return given.length > 0 ? given : [locale];
  });
  const [titles, setTitles] = useState<Texts>(initial.titles);
  const [summaries, setSummaries] = useState<Texts>(initial.summaries);
  const [unit, setUnit] = useState(initial.orgUnitId ?? "");
  const clause = initial.audience.anyOf?.[0];
  const [audienceUnits, setAudienceUnits] = useState<string[]>(clause?.orgUnits?.map((u) => u.id) ?? []);
  const [steps, setSteps] = useState(initial.courses.map((c) => ({ courseId: c.courseId, requiresPrevious: c.requiresPrevious })));
  const [adding, setAdding] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const known = new Map([
    ...courses.map((c) => [c.id, c.titles] as const),
    ...initial.courses.map((c) => [c.courseId, c.titles] as const),
  ]);
  const choices = courses.filter((c) => c.status !== "retired" && !steps.some((s) => s.courseId === c.id));

  async function run(request: () => Promise<Response>, done: string) {
    setBusy(true);
    setMessages([]);
    setNotice(null);
    const result = await call<PathView>(request, t("failed"));
    if (result.ok) {
      setPath(result.value);
      setNotice(done);
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  const body = () => ({
    rowVersion: path.rowVersion,
    orgUnitId: unit || null,
    titles: only(titles, langs),
    summaries: only(summaries, langs),
    audience: audienceUnits.length === 0 ? { anyOf: [] } : { anyOf: [{ orgUnits: audienceUnits.map((id) => ({ id, subtree: true })) }] },
    courses: steps,
  });

  const move = (index: number, by: number) =>
    setSteps((list) => {
      const next = [...list];
      const [moved] = next.splice(index, 1);
      if (moved) next.splice(index + by, 0, moved);
      return next;
    });

  return (
    <form
      className="learning"
      onSubmit={(e) => {
        e.preventDefault();
        void run(() => send(`${learningApi}/manage/paths/${path.id}`, "PUT", body()), t("saved"));
      }}
    >
      <section className="learning-section" aria-labelledby="path-state">
        <h2 id="path-state">{t("state")}</h2>
        <div className="learning-row">
          <Badge tone={statusTones[path.status]}>{t(`status.${path.status}`)}</Badge>
          {path.status === "published" ? <Link href={pathHref(path.id)}>{t("openAsLearner")}</Link> : null}
        </div>
        {notice ? <Alert tone="success" title={notice} live /> : null}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
      </section>
      <fieldset className="learning-section">
        <legend>{t("details")}</legend>
        <div className="learning-row">
          {locales.map((l) => (
            <Checkbox
              key={l}
              name="path-language"
              label={languageName(l, locale)}
              checked={langs.includes(l)}
              disabled={langs.length === 1 && langs.includes(l)}
              onChange={(e) => setLangs(toggle(langs, l, e.target.checked))}
            />
          ))}
        </div>
        {langs.map((l) => (
          <div key={l} className="learning-row">
            <FormField
              label={t("titleIn", { language: languageName(l, locale) })}
              name={`path-title-${l}`}
              value={titles[l] ?? ""}
              maxLength={200}
              lang={l}
              dir="auto"
              onChange={(e) => setTitles({ ...titles, [l]: e.target.value })}
            />
            <TextAreaField
              label={t("summaryIn", { language: languageName(l, locale) })}
              name={`path-summary-${l}`}
              value={summaries[l] ?? ""}
              maxLength={2000}
              rows={2}
              lang={l}
              dir="auto"
              onChange={(e) => setSummaries({ ...summaries, [l]: e.target.value })}
            />
          </div>
        ))}
        <SelectField
          label={t("owner")}
          name="path-unit"
          value={unit}
          onChange={(e) => setUnit(e.target.value)}
          options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
        />
        <p className="learning-meta">{t("audienceHint")}</p>
        <div className="learning-row">
          {units.map((u) => (
            <Checkbox
              key={u.id}
              name="path-audience"
              label={<bdi>{unitLabel(u, locale)}</bdi>}
              checked={audienceUnits.includes(u.id)}
              onChange={(e) => setAudienceUnits(toggle(audienceUnits, u.id, e.target.checked))}
            />
          ))}
        </div>
      </fieldset>
      <fieldset className="learning-section">
        <legend>{t("courses")}</legend>
        <ol className="learning-list" aria-label={t("courses")}>
          {steps.map((step, index) => (
            <li key={step.courseId}>
              <div className="learning-row">
                <span>{t("step", { number: index + 1 })}</span>
                <bdi>{localText(known.get(step.courseId), locale, step.courseId)}</bdi>
                <Button size="xs" variant="quiet" disabled={index === 0} onClick={() => move(index, -1)}>
                  {t("moveUp")}
                </Button>
                <Button size="xs" variant="quiet" disabled={index === steps.length - 1} onClick={() => move(index, 1)}>
                  {t("moveDown")}
                </Button>
                <Button size="xs" variant="destructive" icon="trash" disabled={steps.length === 1} onClick={() => setSteps(steps.filter((s) => s.courseId !== step.courseId))}>
                  {t("remove")}
                </Button>
              </div>
              {index > 0 ? (
                <Checkbox
                  name={`path-requires-${step.courseId}`}
                  label={t("requiresPrevious")}
                  checked={step.requiresPrevious}
                  onChange={(e) => setSteps(steps.map((s) => (s.courseId === step.courseId ? { ...s, requiresPrevious: e.target.checked } : s)))}
                />
              ) : null}
            </li>
          ))}
        </ol>
        {choices.length > 0 ? (
          <div className="learning-row">
            <SelectField
              label={t("addCourse")}
              name="path-add"
              value={adding}
              onChange={(e) => setAdding(e.target.value)}
              options={[{ value: "", label: t("chooseCourse") }, ...choices.map((c) => ({ value: c.id, label: localText(c.titles, locale) }))]}
            />
            <Button
              icon="plus"
              disabled={!adding || steps.length >= 30}
              onClick={() => {
                setSteps([...steps, { courseId: adding, requiresPrevious: true }]);
                setAdding("");
              }}
            >
              {t("add")}
            </Button>
          </div>
        ) : null}
      </fieldset>
      <div className="learning-row">
        <Button type="submit" icon="check" busy={busy}>
          {t("save")}
        </Button>
        {path.status !== "published" ? (
          <Button variant="primary" icon="upload" busy={busy} onClick={() => void run(() => send(`${learningApi}/manage/paths/${path.id}:publish`, "POST"), t("published"))}>
            {t("publish")}
          </Button>
        ) : null}
      </div>
    </form>
  );
}
