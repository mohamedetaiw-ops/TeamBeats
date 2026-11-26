"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, FormPanel, SelectField } from "@/components/ui/FormField";
import { NumberField } from "@/components/ui/Inputs";
import { FactList } from "@/components/ui/Patterns";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import { problemMessages } from "@/lib/notifications/types";
import type { Experiment, ExperimentPage, ExperimentResults } from "@/lib/experiments";

const tone = { proposed: "warning", running: "info", stopped: "neutral" } as const;

/**
 * Governed experience experiments (spec 021 R3, US9 scenario 8, FR-022): propose trying an earlier release of a page on
 * a share of the people who agreed to take part, then a tenant administrator other than the proposer approves it, and
 * anyone who may stops it. Results per arm come from analytics with the minimum group size. Every rule is the API's: the
 * buttons are shown and the API answers who may use them.
 */
export function ExperimentsPanel({
  experiments,
  pages,
  results,
}: {
  experiments: Experiment[];
  pages: ExperimentPage[];
  results: Record<string, ExperimentResults>;
}) {
  const t = useTranslations("analytics.experiments");
  const format = useFormatter();
  const router = useRouter();
  const tried = pages.filter((p) => (p.currentRelease ?? 0) > 1);
  const [pageId, setPageId] = useState(tried[0]?.id ?? "");
  const [name, setName] = useState("");
  const [variant, setVariant] = useState<number | null>(1);
  const [percent, setPercent] = useState<number | null>(10);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string[] | null>(null);
  const [status, setStatus] = useState("");

  async function call(key: string, url: string, body: unknown, done: string) {
    setBusy(key);
    setError(null);
    try {
      const response = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
      if (!response.ok) {
        setError(response.status === 403 ? [t("notAllowed")] : await problemMessages(response));
        return;
      }
      setStatus(done);
      router.refresh();
    } catch {
      setError([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function propose(e: FormEvent) {
    e.preventDefault();
    void call("propose", "/api/v1/experience/experiments", { pageId, name, variantRelease: variant, percent }, t("proposed"));
  }

  const number = (n: number | null, size: number) => (n === null ? t("hidden", { size }) : format.number(n));

  return (
    <div className="analytics-item" data-testid="experiments">
      <FormPanel as="form" aria-labelledby="experiment-new" onSubmit={propose}>
        <h2 id="experiment-new">{t("new")}</h2>
        <p className="analytics-meta">{t("newHint")}</p>
        {tried.length === 0 ? (
          <p className="analytics-meta">{t("noPages")}</p>
        ) : (
          <>
            <SelectField
              label={t("page")}
              name="page"
              value={pageId}
              onChange={(e) => setPageId(e.target.value)}
              options={tried.map((p) => ({ value: p.id, label: t("pageOption", { slug: p.slug, release: String(p.currentRelease ?? 0) }) }))}
            />
            <FormField label={t("name")} name="name" value={name} maxLength={120} required onChange={(e) => setName(e.target.value)} />
            <NumberField label={t("variantRelease")} hint={t("variantReleaseHint")} name="variant" value={variant} onValueChange={setVariant} min={1} />
            <NumberField label={t("percent")} hint={t("percentHint")} name="percent" value={percent} onValueChange={setPercent} min={1} max={50} unit="%" />
            <div className="button-row">
              <Button type="submit" variant="primary" busy={busy === "propose"} busyLabel={t("busy")}>
                {t("propose")}
              </Button>
            </div>
          </>
        )}
      </FormPanel>

      <p className="visually-hidden" role="status">
        {status}
      </p>
      {error ? (
        <div role="alert" className="notice notice-error">
          {error.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}

      {experiments.length === 0 ? (
        <StateView kind="empty" title={t("none")} message={t("noneHint")} />
      ) : (
        experiments.map((x) => {
          const r = results[x.id];
          return (
            <Widget key={x.id} title={x.name} count={<Badge tone={tone[x.status]}>{t(`status.${x.status}`)}</Badge>} testId={`experiment-${x.id}`}>
              <FactList
                facts={[
                  { key: "page", label: t("page"), value: <bdi>{x.slug}</bdi> },
                  { key: "arms", label: t("arms"), value: t("armsValue", { control: String(x.controlRelease), variant: String(x.variantRelease) }) },
                  { key: "share", label: t("percent"), value: format.number(x.percent / 100, { style: "percent" }) },
                  { key: "proposed", label: t("proposedAt"), value: format.dateTime(new Date(x.proposedAt), { dateStyle: "medium" }) },
                ]}
              />
              {r ? (
                <div className="analytics-table">
                  <table>
                    <caption>{t("results", { size: r.minGroupSize })}</caption>
                    <thead>
                      <tr>
                        <th scope="col">{t("column.arm")}</th>
                        <th scope="col">{t("column.people")}</th>
                        <th scope="col">{t("column.completed")}</th>
                        <th scope="col">{t("column.opened")}</th>
                        <th scope="col">{t("column.rate")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {r.arms.map((a) => (
                        <tr key={a.arm}>
                          <th scope="row">{t(`arm.${a.arm}`)}</th>
                          <td data-number>{format.number(a.people)}</td>
                          <td data-number>{number(a.completedTask, r.minGroupSize)}</td>
                          <td data-number>{number(a.openedItem, r.minGroupSize)}</td>
                          <td data-number>
                            {a.completionRate === null ? t("hidden", { size: r.minGroupSize }) : format.number(a.completionRate, { style: "percent", maximumFractionDigits: 1 })}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
              {x.status !== "stopped" ? (
                <div className="button-row">
                  {x.status === "proposed" ? (
                    <Button variant="primary" busy={busy === `approve-${x.id}`} busyLabel={t("busy")}
                      onClick={() => void call(`approve-${x.id}`, `/api/v1/experience/experiments/${x.id}/approve`, {}, t("approved"))}>
                      {t("approve")}
                    </Button>
                  ) : null}
                  <Button variant="secondary" busy={busy === `stop-${x.id}`} busyLabel={t("busy")}
                    onClick={() => void call(`stop-${x.id}`, `/api/v1/experience/experiments/${x.id}/stop`, {}, t("stopped"))}>
                    {t("stop")}
                  </Button>
                </div>
              ) : null}
            </Widget>
          );
        })
      )}
    </div>
  );
}
