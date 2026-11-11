"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { problemMessages } from "@/lib/notifications/types";
import {
  CLOCK_FILE_ERRORS,
  CLOCK_FILE_STATUSES,
  CLOCK_REASONS,
  checkResult,
  known,
  type ClockFile,
  type ClockFilePage,
  type ClockProblem,
} from "@/lib/integrations/types";
import { Messages } from "./parts";
import { send } from "./request";

/**
 * Clock data files of a connection (spec 017 US7 scenario 1): upload a CSV file, import what is waiting now, and see
 * each file with its counts and the rows that were not imported, by line and reason. Imports run as connector jobs, so
 * a file shows "waiting" until the next job round.
 */
export function ClockFilesPanel({ connectionId, initial }: { connectionId: string; initial: ClockFilePage | null }) {
  const t = useTranslations("connections.clock.files");
  const format = useFormatter();
  const base = `/api/v1/integrations/pack-connections/${connectionId}/clock-files`;
  const returnTo = `/admin/connections/${connectionId}`;
  const [files, setFiles] = useState<ClockFile[]>(initial?.items ?? []);
  const [cursor, setCursor] = useState(initial?.nextCursor ?? null);
  const [uploads, setUploads] = useState<UploadItem[]>([]);
  const [problems, setProblems] = useState<Record<string, ClockProblem[]>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });

  if (!initial) {
    return (
      <section className="connections-section" aria-labelledby="clock-files-heading">
        <h2 id="clock-files-heading">{t("title")}</h2>
        <p className="notice">{t("unavailable")}</p>
      </section>
    );
  }

  async function reload(more: string | null) {
    const response = await fetch(more ? `${base}?cursor=${encodeURIComponent(more)}` : base);
    if (!response.ok) return;
    const page = (await response.json()) as ClockFilePage;
    setFiles((f) => (more ? [...f, ...page.items] : page.items));
    setCursor(page.nextCursor);
  }

  async function upload(file: File) {
    const key = `${file.name}-${file.size}-${file.lastModified}`;
    setUploads((u) => [...u.filter((x) => x.key !== key), { key, name: file.name, size: file.size, progress: 0 }]);
    setMessages({ ok: true, list: [] });
    try {
      const response = await fetch(`${base}?name=${encodeURIComponent(file.name)}`, { method: "POST", headers: { "content-type": "text/csv" }, body: file });
      if (!response.ok) {
        const list = await problemMessages(response);
        setUploads((u) => u.map((x) => (x.key === key ? { ...x, progress: undefined, error: list[0] ?? t("failed") } : x)));
        return;
      }
      setUploads((u) => u.filter((x) => x.key !== key));
      setMessages({ ok: true, list: [t("uploaded", { name: file.name })] });
      await reload(null);
    } catch {
      setUploads((u) => u.map((x) => (x.key === key ? { ...x, progress: undefined, error: t("failed") } : x)));
    }
  }

  async function importNow() {
    setBusy("import");
    setMessages({ ok: true, list: [] });
    const result = await send<unknown>("POST", `${base}:import`, undefined, returnTo, t("failed"));
    setBusy(null);
    setMessages(result.ok ? { ok: true, list: [t("queued")] } : { ok: false, list: result.messages });
  }

  async function showProblems(fileId: string) {
    setBusy(`problems-${fileId}`);
    try {
      const response = await fetch(`${base}/${fileId}/unmatched`);
      if (response.ok) {
        const list = (await response.json()) as ClockProblem[];
        setProblems((p) => ({ ...p, [fileId]: list }));
      } else {
        setMessages({ ok: false, list: [t("failed")] });
      }
    } catch {
      setMessages({ ok: false, list: [t("failed")] });
    } finally {
      setBusy(null);
    }
  }

  const date = (iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium", timeStyle: "short" });

  return (
    <section className="connections-section" aria-labelledby="clock-files-heading">
      <h2 id="clock-files-heading">{t("title")}</h2>
      <p>{t("intro")}</p>
      <FileUpload
        label={t("upload")}
        hint={t("uploadHint")}
        accept=".csv,.txt,text/csv"
        multiple={false}
        items={uploads}
        onAdd={(chosen) => void Promise.all(chosen.map((f) => upload(f)))}
        onRemove={(key) => setUploads((u) => u.filter((x) => x.key !== key))}
      />
      <div className="connections-row">
        <Button busy={busy === "import"} busyLabel={t("working")} onClick={() => void importNow()}>
          {t("importNow")}
        </Button>
        <Button variant="quiet" onClick={() => void reload(null)}>
          {t("refresh")}
        </Button>
      </div>
      <Messages messages={messages.list} ok={messages.ok} />
      {files.length === 0 ? (
        <p className="notice">{t("empty")}</p>
      ) : (
        <ul className="connections-list">
          {files.map((f) => {
            const error = f.errorKey ? checkResult(f.errorKey) : null;
            return (
              <li key={f.id} className="connections-item" data-status={f.status}>
                <h3 className="connections-row">
                  <bdi>{f.name}</bdi>
                  <span className="connections-status" data-status={f.status}>
                    {known(CLOCK_FILE_STATUSES, f.status) ? t(`statuses.${f.status}`) : f.status}
                  </span>
                </h3>
                <p className="connections-row">
                  <span>{t(f.source === "sftp" ? "fromSftp" : "uploadedFile")}</span>
                  <span>{t("received", { at: date(f.receivedAt) })}</span>
                </p>
                {error ? <p>{known(CLOCK_FILE_ERRORS, error) ? t(`errors.${error}`) : error}</p> : null}
                {f.status === "imported" ? (
                  <p className="connections-row">
                    <span>{t("rows", { count: f.rowCount })}</span>
                    <span>{t("imported", { count: f.imported })}</span>
                    <span>{t("duplicates", { count: f.duplicates })}</span>
                    <span>{t("unmatched", { count: f.unmatched })}</span>
                  </p>
                ) : null}
                {f.unmatched > 0 && !problems[f.id] ? (
                  <Button variant="quiet" busy={busy === `problems-${f.id}`} busyLabel={t("working")} onClick={() => void showProblems(f.id)}>
                    {t("showUnmatched")}
                  </Button>
                ) : null}
                {problems[f.id] ? (
                  <div className="connections-table">
                    <table data-testid="clock-unmatched">
                      <caption>{t("unmatchedCaption", { name: f.name })}</caption>
                      <thead>
                        <tr>
                          <th scope="col">{t("line")}</th>
                          <th scope="col">{t("employeeNumber")}</th>
                          <th scope="col">{t("reason")}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {problems[f.id]!.map((p) => (
                          <tr key={p.line}>
                            <td data-number="">{format.number(p.line)}</td>
                            <td>{p.employeeNumber ? <bdi dir="ltr">{p.employeeNumber}</bdi> : t("noNumber")}</td>
                            <td>{known(CLOCK_REASONS, p.reason) ? t(`reasons.${p.reason}`) : p.reason}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                    {f.unmatched > problems[f.id]!.length ? <p className="field-hint">{t("firstRows", { count: problems[f.id]!.length })}</p> : null}
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}
      {cursor ? (
        <Button busy={busy === "more"} busyLabel={t("working")} onClick={() => (setBusy("more"), void reload(cursor).finally(() => setBusy(null)))}>
          {t("more")}
        </Button>
      ) : null}
    </section>
  );
}
