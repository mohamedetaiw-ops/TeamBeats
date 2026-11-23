"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import type { PaperImportView } from "@/lib/policies/types";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { FileUpload } from "@/components/ui/FileUpload";
import "./policies.css";

/**
 * Paper attestation import (spec 025 section 14): the scanned signed forms first, then a CSV with the columns email,
 * attested_on and optionally version. The API checks the files and every line, and reports refused lines by number.
 */
export function PaperImport({ policyId, imports, canImport }: { policyId: string; imports: PaperImportView[]; canImport: boolean }) {
  const t = useTranslations("policies.paper");
  const format = useFormatter();
  const router = useRouter();
  const ids = useId();
  const [scan, setScan] = useState<File | null>(null);
  const [csv, setCsv] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const base = `/api/v1/policies/${encodeURIComponent(policyId)}/paper-imports`;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!scan || !csv) {
      setMessages([t("chooseBoth")]);
      return;
    }
    setBusy(true);
    setMessages([]);
    try {
      const attached = await fetch(`${base}?name=${encodeURIComponent(scan.name)}`, { method: "POST", headers: { "content-type": scan.type || "application/octet-stream" }, body: scan });
      if (!attached.ok) {
        setMessages(await problemMessages(attached));
        return;
      }
      const draft = (await attached.json()) as PaperImportView;
      const rows = await fetch(`${base}/${encodeURIComponent(draft.id)}/rows`, { method: "POST", headers: { "content-type": "text/csv" }, body: await csv.text() });
      if (!rows.ok) {
        setMessages(await problemMessages(rows));
        return;
      }
      setScan(null);
      setCsv(null);
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  const day = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" });

  return (
    <>
      {canImport ? (
        <form className="policies-card" aria-labelledby={`${ids}-new`} onSubmit={(e) => void submit(e)} noValidate data-testid="paper-form">
          <h2 id={`${ids}-new`}>{t("newTitle")}</h2>
          <p className="policies-meta">{t("intro")}</p>
          <FileUpload
            label={t("scan")}
            hint={t("scanHint")}
            accept="application/pdf,image/png,image/jpeg"
            multiple={false}
            items={scan ? [{ key: "scan", name: scan.name, size: scan.size }] : []}
            onAdd={(files) => setScan(files[0] ?? null)}
            onRemove={() => setScan(null)}
          />
          <FileUpload
            label={t("csv")}
            hint={t("csvHint")}
            accept=".csv,text/csv"
            multiple={false}
            items={csv ? [{ key: "csv", name: csv.name, size: csv.size }] : []}
            onAdd={(files) => setCsv(files[0] ?? null)}
            onRemove={() => setCsv(null)}
          />
          {messages.length > 0 ? (
            <div className="notice notice-error" role="alert">
              {messages.map((m) => (
                <p key={m}>{m}</p>
              ))}
            </div>
          ) : null}
          <div className="button-row">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
              {t("import")}
            </Button>
          </div>
        </form>
      ) : null}
      <section className="policies-card" aria-labelledby={`${ids}-list`}>
        <h2 id={`${ids}-list`}>{t("listTitle")}</h2>
        {imports.length === 0 ? (
          <p className="policies-meta">{t("none")}</p>
        ) : (
          <ul className="policies-list" data-testid="paper-imports">
            {imports.map((i) => (
              <li key={i.id} className="policies-option" data-testid="paper-import">
                <p>
                  <bdi>{t("importedBy", { name: i.importedBy.displayName, date: day(i.createdAt) })}</bdi>{" "}
                  <Badge tone={i.status === "completed" ? "success" : "neutral"}>{t(`statuses.${i.status}`)}</Badge>
                </p>
                <p>{t("counts", { imported: i.imported, rejected: i.rejected.length })}</p>
                {i.rejected.length > 0 ? (
                  <ul className="policies-list" aria-label={t("rejectedLabel")}>
                    {i.rejected.map((r) => (
                      <li key={`${r.line}-${r.key}`}>{t("line", { line: r.line, reason: r.message ?? r.key })}</li>
                    ))}
                  </ul>
                ) : null}
                <div className="button-row">
                  <ButtonLink size="sm" icon="download" href={`${base}/${encodeURIComponent(i.id)}/scan`} download>
                    <bdi>{t("openScan", { name: i.scanName })}</bdi>
                  </ButtonLink>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}
