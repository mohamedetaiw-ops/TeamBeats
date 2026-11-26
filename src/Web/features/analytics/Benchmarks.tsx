"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import type { BenchmarksView } from "@/lib/analytics/types";
import { problemMessages } from "@/lib/notifications/types";
import { Alert } from "@/components/ui/Alert";
import { StateView } from "@/components/ui/StateView";
import { Switch } from "@/components/ui/Switch";

/**
 * Benchmarks across anonymized organizations (spec 021 R3). A tenant administrator chooses to take part; the server
 * decides what is shown: the organization's own figure, how many organizations took part, and quartiles only from the
 * minimum number of organizations. No other organization is ever named.
 */
export function Benchmarks({ view }: { view: BenchmarksView }) {
  const t = useTranslations("analytics.benchmarks");
  const format = useFormatter();
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [on, setOn] = useState(view.enabled);
  const value = (n: number | null) => (n === null ? t("hidden") : format.number(n / 100, { style: "percent", maximumFractionDigits: 1 }));

  async function change(next: boolean) {
    setBusy(true);
    setError(null);
    setOn(next);
    try {
      const response = await fetch("/api/v1/analytics/benchmarks", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ enabled: next }),
      });
      if (!response.ok) {
        setOn(!next);
        setError((await problemMessages(response))[0] ?? t("switchFailed"));
        return;
      }
      router.refresh();
    } catch {
      setOn(!next);
      setError(t("switchFailed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="analytics-item" data-testid="benchmarks">
      {view.canChange ? (
        <section className="analytics-card" aria-labelledby="benchmarks-setting">
          <h2 id="benchmarks-setting">{t("setting")}</h2>
          <Switch label={t("switch")} hint={t("switchHint", { count: view.minTenants })} checked={on} disabled={busy} onChange={(e) => void change(e.target.checked)} />
          {error ? <Alert tone="danger" title={error} live /> : null}
        </section>
      ) : null}
      {!view.available ? (
        <StateView kind="empty" title={t("unavailable")} message={t("unavailableHint")} />
      ) : !view.enabled ? (
        <StateView kind="empty" title={t("off")} message={t("offHint")} />
      ) : (
        <section className="analytics-card" aria-labelledby="benchmarks-figures">
          <h2 id="benchmarks-figures">{t("figures")}</h2>
          <p className="analytics-meta">{t("figuresHint", { count: view.minTenants })}</p>
          <div className="analytics-table">
            <table data-testid="benchmark-figures">
              <thead>
                <tr>
                  <th scope="col">{t("column.metric")}</th>
                  <th scope="col">{t("column.week")}</th>
                  <th scope="col">{t("column.own")}</th>
                  <th scope="col">{t("column.tenants")}</th>
                  <th scope="col">{t("column.lower")}</th>
                  <th scope="col">{t("column.median")}</th>
                  <th scope="col">{t("column.upper")}</th>
                </tr>
              </thead>
              <tbody>
                {view.figures.map((f) => (
                  <tr key={f.metric}>
                    <th scope="row">{t(`metric.${f.metric}`)}</th>
                    <td>{format.dateTime(new Date(`${f.week}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" })}</td>
                    <td data-number>{value(f.own)}</td>
                    <td data-number>{format.number(f.tenants)}</td>
                    <td data-number>{value(f.lower)}</td>
                    <td data-number>{value(f.median)}</td>
                    <td data-number>{value(f.upper)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}
