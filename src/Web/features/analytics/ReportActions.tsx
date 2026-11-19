"use client";

import { useEffect, useId, useState, type FormEvent } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import {
  exportsApi,
  reportHref,
  reportsApi,
  schedulesApi,
  reportDefinitionEvent,
  summariesApi,
  type DashboardSummary,
  type ReportDefinition,
  type ReportOptions,
} from "@/lib/analytics/reports";
import { problemMessages } from "@/lib/notifications/types";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Dialog } from "@/components/ui/Dialog";
import { FormField, SelectField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";

/** The definition last announced by the dashboard on the page, starting from the page's own. */
function useReportDefinition(initial: ReportDefinition | null): ReportDefinition | null {
  const [definition, setDefinition] = useState(initial);
  useEffect(() => {
    const listen = (e: Event) => setDefinition((e as CustomEvent<ReportDefinition>).detail);
    window.addEventListener(reportDefinitionEvent, listen);
    return () => window.removeEventListener(reportDefinitionEvent, listen);
  }, []);
  return definition;
}

const pick = (names: Record<string, string>, locale: string) => names[locale] ?? names.en ?? Object.values(names)[0] ?? "";

type Result = { ok: boolean; messages: string[]; link?: string };

/**
 * Export and schedule actions for one dashboard (spec 021 US7). The API says whether the package has exports and
 * schedules; without them the buttons are not shown. A dashboard whose filters change announces its definition, so
 * the actions always export what is on screen.
 */
export function ReportActions({ definition: initial }: { definition: ReportDefinition | null }) {
  const t = useTranslations("analytics.reports");
  const locale = useLocale();
  const definition = useReportDefinition(initial);
  const [options, setOptions] = useState<ReportOptions | null>(null);
  const [open, setOpen] = useState<"export" | "schedule" | null>(null);
  const [name, setName] = useState("");
  const [format, setFormat] = useState<"csv" | "xlsx">("xlsx");
  const [cadence, setCadence] = useState<"weekly" | "monthly">("weekly");
  const [unit, setUnit] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result | null>(null);
  const formId = useId();

  useEffect(() => {
    const controller = new AbortController();
    fetch(`${reportsApi}/options`, { signal: controller.signal })
      .then(async (response) => (response.ok ? ((await response.json()) as ReportOptions) : null))
      .then(setOptions, () => setOptions(null));
    return () => controller.abort();
  }, []);

  if (!options || !definition || (!options.canExport && !options.canSchedule && !options.canSummarize)) return null;

  function show(kind: "export" | "schedule") {
    setResult(null);
    setName("");
    setOpen(kind);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!definition) return;
    setBusy(true);
    setResult(null);
    try {
      // A schedule always sends the last week or month, so the dates on screen are left out.
      const scheduled: ReportDefinition = { ...definition, filters: definition.filters ? { ...definition.filters, from: undefined, to: undefined } : null };
      const response =
        open === "export"
          ? await fetch(exportsApi, {
              method: "POST",
              headers: { "content-type": "application/json" },
              body: JSON.stringify({ definition, format, name: name.trim() || null }),
            })
          : await fetch(schedulesApi, {
              method: "POST",
              headers: { "content-type": "application/json" },
              body: JSON.stringify({ definition: scheduled, cadence, name: name.trim() || null, recipientUnitId: unit || null }),
            });
      if (!response.ok) {
        const messages = await problemMessages(response);
        setResult({ ok: false, messages: messages.length > 0 ? messages : [t("failed")] });
        return;
      }
      const body = (await response.json()) as { id: string };
      setResult(
        open === "export"
          ? { ok: true, messages: [t("exportQueued")], link: reportHref(body.id) }
          : { ok: true, messages: [t("scheduled")], link: "/analytics/reports" },
      );
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      {options.canExport ? (
        <Button size="sm" icon="download" onClick={() => show("export")}>
          {t("export")}
        </Button>
      ) : null}
      {options.canSchedule ? (
        <Button size="sm" icon="calendar" onClick={() => show("schedule")}>
          {t("schedule")}
        </Button>
      ) : null}
      {options.canSummarize ? <SummaryAction definition={definition} /> : null}
      <Dialog
        open={open !== null}
        onClose={() => setOpen(null)}
        title={open === "export" ? t("exportTitle") : t("scheduleTitle")}
        description={open === "export" ? t("exportIntro") : t("scheduleIntro")}
        closeLabel={t("close")}
        footer={
          result?.ok ? (
            <ButtonLink href={result.link ?? "/analytics/reports"} variant="primary">
              {t("openReports")}
            </ButtonLink>
          ) : (
            <Button type="submit" form={formId} variant="primary" busy={busy} busyLabel={t("busy")}>
              {open === "export" ? t("exportSubmit") : t("scheduleSubmit")}
            </Button>
          )
        }
      >
        <form id={formId} className="analytics-report-form" onSubmit={(e) => void submit(e)}>
          <FormField label={t("name")} hint={t("nameHint")} name="name" value={name} maxLength={120} onChange={(e) => setName(e.target.value)} />
          {open === "export" ? (
            <RadioGroup
              legend={t("format")}
              name="format"
              value={format}
              onChange={(e) => setFormat(e.target.value as "csv" | "xlsx")}
              options={[
                { value: "xlsx", label: t("formatXlsx") },
                { value: "csv", label: t("formatCsv") },
              ]}
            />
          ) : (
            <>
              <RadioGroup
                legend={t("cadence")}
                name="cadence"
                value={cadence}
                onChange={(e) => setCadence(e.target.value as "weekly" | "monthly")}
                options={[
                  { value: "weekly", label: t("cadenceWeekly"), hint: t("cadenceWeeklyHint") },
                  { value: "monthly", label: t("cadenceMonthly"), hint: t("cadenceMonthlyHint") },
                ]}
              />
              <SelectField
                label={t("recipients")}
                hint={t("recipientsHint")}
                name="recipients"
                value={unit}
                onChange={(e) => setUnit(e.target.value)}
                options={[{ value: "", label: t("recipientsMe") }, ...options.units.map((u) => ({ value: u.id, label: pick(u.names, locale) || u.id }))]}
              />
            </>
          )}
          {result ? (
            <Alert tone={result.ok ? "success" : "danger"} title={result.messages[0] ?? ""} live>
              {result.messages.slice(1).map((m) => (
                <p key={m}>{m}</p>
              ))}
            </Alert>
          ) : null}
        </form>
      </Dialog>
    </>
  );
}

/**
 * Summarize (spec 021 US8 scenario 3): asks the AI Gateway for a short description of the dashboard on screen and shows
 * it labelled as AI-generated, with the figures it used. Nothing is kept; asking again makes a new one.
 */
function SummaryAction({ definition }: { definition: ReportDefinition }) {
  const t = useTranslations("analytics.reports");
  const locale = useLocale();
  const format = useFormatter();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [summary, setSummary] = useState<DashboardSummary | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setOpen(true);
    setBusy(true);
    setSummary(null);
    setError(null);
    try {
      const response = await fetch(summariesApi, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ definition, language: locale === "ar" ? "ar" : "en" }),
      });
      if (!response.ok) {
        setError((await problemMessages(response))[0] ?? t("summary.failed"));
        return;
      }
      setSummary((await response.json()) as DashboardSummary);
    } catch {
      setError(t("summary.failed"));
    } finally {
      setBusy(false);
    }
  }

  const denial = summary?.denial ? (t.has(`summary.denial.${summary.denial}`) ? t(`summary.denial.${summary.denial}`) : t("summary.denial.other")) : null;
  return (
    <>
      <Button size="sm" icon="spark" onClick={() => void run()} busy={busy && !open} busyLabel={t("summary.busy")}>
        {t("summary.action")}
      </Button>
      <Dialog
        open={open}
        onClose={() => setOpen(false)}
        title={t("summary.title")}
        description={t("summary.intro")}
        closeLabel={t("summary.close")}
        footer={
          <Button variant="primary" icon="spark" onClick={() => void run()} busy={busy} busyLabel={t("summary.busy")}>
            {t("summary.again")}
          </Button>
        }
      >
        <div className="analytics-summary" data-testid="analytics-summary" aria-live="polite" aria-busy={busy}>
          {busy ? <p>{t("summary.busy")}</p> : null}
          {error ? <Alert tone="danger" title={error} /> : null}
          {denial ? <Alert tone="warning" title={t("summary.none")}><p>{denial}</p></Alert> : null}
          {summary?.text ? (
            <>
              <Badge tone="accent" icon="spark">
                {t("summary.aiGenerated")}
              </Badge>
              <p>
                <bdi>{summary.text}</bdi>
              </p>
              <h3>{t("summary.figures")}</h3>
              <ul className="analytics-summary-figures">
                {summary.figures.map((f) => (
                  <li key={f.label}>
                    <bdi dir="ltr">{f.label}</bdi> <span data-number>{format.number(f.value, { maximumFractionDigits: 4 })}</span>
                  </li>
                ))}
              </ul>
              <p className="analytics-note">{t("summary.check")}</p>
            </>
          ) : null}
        </div>
      </Dialog>
    </>
  );
}
