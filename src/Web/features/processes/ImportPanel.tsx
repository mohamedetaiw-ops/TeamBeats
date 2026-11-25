"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { FormField, SelectField } from "@/components/ui/FormField";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { problemMessages } from "@/lib/notifications/types";
import type { ImportItem, ProcessImportReport } from "@/lib/processes";
import "./processes.css";

/**
 * Import of a .bpmn file exported from Camunda Modeler or another tool (spec 024 R3, US7 scenario 1). The API converts
 * what it can and lists what it cannot; it creates the draft only when that list is empty, and checks the package and
 * the designer's units. This panel only sends the file and shows the report.
 */
export function ImportPanel({ units }: { units: OrgUnit[] }) {
  const t = useTranslations("processes.import");
  const locale = useLocale();
  const router = useRouter();
  const [key, setKey] = useState("");
  const [owner, setOwner] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [report, setReport] = useState<ProcessImportReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  const items: UploadItem[] = file ? [{ key: "file", name: file.name, size: file.size }] : [];

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!file || !key.trim()) return;
    setBusy(true);
    setMessages([]);
    setReport(null);
    try {
      const bpmnXml = await file.text();
      const response = await fetch(`/api/v1/workflow/processes/${encodeURIComponent(key.trim())}:import`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ bpmnXml, ownerOrgUnitId: owner || null }),
      });
      if (!response.ok) {
        const list = await problemMessages(response);
        setMessages(list.length > 0 ? list : [t("failed")]);
        return;
      }
      const next = (await response.json()) as ProcessImportReport;
      setReport(next);
      if (next.imported) router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const table = (caption: string, rows: ImportItem[]) => (
    <DataTable
      caption={caption}
      rows={rows}
      rowKey={(r) => `${r.elementId}|${r.key}|${r.detail ?? ""}`}
      emptyTitle={t("nothing")}
      columns={[
        { key: "element", header: t("element"), rowHeader: true, cell: (r) => <bdi className="processes-code">{r.elementId || t("wholeFile")}</bdi> },
        { key: "message", header: t("what"), cell: (r) => r.message },
        { key: "detail", header: t("detail"), cell: (r) => (r.detail ? <bdi className="processes-code">{r.detail}</bdi> : "") },
      ]}
    />
  );

  return (
    <section className="processes-section" aria-labelledby="process-import">
      <h2 id="process-import">{t("title")}</h2>
      <p className="field-hint">{t("intro")}</p>
      <form className="processes-import" onSubmit={submit}>
        <FileUpload
          label={t("file")}
          hint={t("fileHint")}
          accept=".bpmn,.xml,application/xml,text/xml"
          multiple={false}
          items={items}
          onAdd={(files) => setFile(files[0] ?? null)}
          onRemove={() => setFile(null)}
        />
        <div className="processes-filters">
          <FormField label={t("key")} hint={t("keyHint")} name="import-key" dir="ltr" value={key} onChange={(e) => setKey(e.target.value)} maxLength={64} />
          <SelectField
            label={t("owner")}
            name="import-owner"
            value={owner}
            onChange={(e) => setOwner(e.target.value)}
            options={[{ value: "", label: t("wholeOrganization") }, ...units.map((u) => ({ value: u.id, label: unitName(u, locale) }))]}
          />
          <Button type="submit" variant="secondary" busy={busy} busyLabel={t("importing")} disabled={!file || !key.trim()}>
            {t("submit")}
          </Button>
        </div>
      </form>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {report ? (
        <>
          {report.imported ? (
            <Alert tone="success" title={t("importedTitle")} live>
              {t("imported")}
            </Alert>
          ) : (
            <Alert tone="warning" title={t("refusedTitle")} live>
              {t("refused", { count: report.unsupported.length })}
            </Alert>
          )}
          {report.unsupported.length > 0 ? table(t("unsupportedTitle"), report.unsupported) : null}
          {report.converted.length > 0 ? table(t("convertedTitle"), report.converted) : null}
        </>
      ) : null}
    </section>
  );
}
