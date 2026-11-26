"use client";

import { useState, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { MAX_KPI_TILES, type KpiTileSetView } from "@/lib/analytics/types";
import { problemMessages } from "@/lib/notifications/types";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";

type Row = { id: string | null; metric: string; en: string; ar: string; permission: string };

/**
 * The KPI tiles of location dashboards (spec 021 R3, US6 scenario 2): for each tile, the metric name the connected
 * system sends through its KPI feed, the tile's name in each language, and optionally a permission viewers must also
 * hold. Saved as a new version each time; the API checks every value.
 */
export function KpiTilesForm({ view, permissions }: { view: KpiTileSetView; permissions: { value: string; label: string }[] }) {
  const t = useTranslations("analytics.kpiTiles");
  const router = useRouter();
  const [rows, setRows] = useState<Row[]>(() =>
    view.tiles.map((tile) => ({ id: tile.id, metric: tile.metric, en: tile.names.en ?? "", ar: tile.names.ar ?? "", permission: tile.permission ?? "" })),
  );
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);
  const set = (i: number, change: Partial<Row>) => setRows((list) => list.map((r, j) => (j === i ? { ...r, ...change } : r)));

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setResult(null);
    try {
      const tiles = rows.map((r) => ({ id: r.id, metric: r.metric.trim(), names: { en: r.en.trim(), ar: r.ar.trim() }, permission: r.permission || null }));
      const response = await fetch("/api/v1/analytics/kpi-tiles", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ tiles, version: view.version }),
      });
      if (!response.ok) {
        setResult({ ok: false, messages: await problemMessages(response) });
        return;
      }
      setResult({ ok: true, messages: [t("saved")] });
      router.refresh();
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  const options = [{ value: "", label: t("noPermission") }, ...permissions];
  return (
    <form className="analytics-card" onSubmit={save} data-testid="kpi-tiles-form">
      {result ? <Alert tone={result.ok ? "success" : "danger"} title={result.messages.join(" ")} /> : null}
      {rows.length === 0 ? <p className="analytics-meta">{t("none")}</p> : null}
      {rows.map((r, i) => (
        <fieldset key={r.id ?? `new-${i}`} className="analytics-card" data-testid="kpi-tile">
          <legend>{t("tile", { number: String(i + 1) })}</legend>
          <FormField label={t("metric")} hint={t("metricHint")} name={`metric-${i}`} dir="ltr" required value={r.metric} onChange={(e) => set(i, { metric: e.target.value })} disabled={!view.canChange} />
          <FormField label={t("nameEn")} name={`en-${i}`} dir="ltr" lang="en" required maxLength={80} value={r.en} onChange={(e) => set(i, { en: e.target.value })} disabled={!view.canChange} />
          <FormField label={t("nameAr")} name={`ar-${i}`} dir="rtl" lang="ar" required maxLength={80} value={r.ar} onChange={(e) => set(i, { ar: e.target.value })} disabled={!view.canChange} />
          <SelectField label={t("permission")} hint={t("permissionHint")} name={`permission-${i}`} options={options} value={r.permission} onChange={(e) => set(i, { permission: e.target.value })} disabled={!view.canChange} />
          {view.canChange ? (
            <Button variant="quiet" size="sm" onClick={() => setRows((list) => list.filter((_, j) => j !== i))}>
              {t("remove")}
            </Button>
          ) : null}
        </fieldset>
      ))}
      {view.canChange ? (
        <div>
          {rows.length < MAX_KPI_TILES ? (
            <Button onClick={() => setRows((list) => [...list, { id: null, metric: "", en: "", ar: "", permission: "" }])}>{t("add")}</Button>
          ) : null}
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
            {t("save")}
          </Button>
        </div>
      ) : (
        <p className="analytics-meta">{t("readOnly")}</p>
      )}
    </form>
  );
}
