"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { SelectField } from "@/components/ui/FormField";
import { Progress } from "@/components/ui/Status";
import { problemMessages } from "@/lib/notifications/types";
import {
  documentHref,
  importApi,
  importHref,
  importRowStates,
  type FolderView,
  type ImportRowPage,
  type ImportRowState,
  type ImportView,
} from "@/lib/documents/types";
import "./documents.css";

const tones: Record<
  ImportRowState,
  "neutral" | "success" | "warning" | "danger"
> = {
  pending: "neutral",
  imported: "success",
  incomplete: "warning",
  skipped: "neutral",
  failed: "danger",
  quarantined: "danger",
};

/** Library managers upload a ZIP with its manifest; the API checks the manifest before anything is queued. */
export function ImportStart({
  libraryId,
  folders,
}: {
  libraryId: string;
  folders: FolderView[];
}) {
  const t = useTranslations("documents.imports");
  const router = useRouter();
  const [file, setFile] = useState<File | null>(null);
  const [folder, setFolder] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const items: UploadItem[] = file
    ? [{ key: "zip", name: file.name, size: file.size }]
    : [];

  async function start() {
    if (!file) return;
    setBusy(true);
    setMessages([]);
    try {
      const query = new URLSearchParams({ name: file.name });
      if (folder) query.set("folder", folder);
      const response = await fetch(
        `/api/v1/documents/libraries/${encodeURIComponent(libraryId)}/imports?${query}`,
        {
          method: "POST",
          headers: { "content-type": "application/zip" },
          body: file,
        },
      );
      if (response.ok) {
        const view = (await response.json()) as ImportView;
        router.push(importHref(view.id));
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
    <form
      className="documents-card"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void start();
      }}
    >
      <h2>{t("startTitle")}</h2>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <FileUpload
        label={t("zip")}
        hint={t("zipHint")}
        accept=".zip"
        multiple={false}
        items={items}
        onAdd={(f) => setFile(f[0] ?? null)}
        onRemove={() => setFile(null)}
      />
      <SelectField
        label={t("folder")}
        name="import-folder"
        value={folder}
        onChange={(e) => setFolder(e.target.value)}
        options={[
          { value: "", label: t("libraryRoot") },
          ...folders.map((f) => ({ value: f.id, label: f.name })),
        ]}
      />
      <Button
        type="submit"
        variant="primary"
        disabled={!file}
        busy={busy}
        busyLabel={t("uploading")}
      >
        {t("start")}
      </Button>
    </form>
  );
}

/** How to write the manifest: one row per file, the columns the API reads. */
export function ManifestGuide() {
  const t = useTranslations("documents.imports");
  const columns = [
    "path",
    "folder",
    "name",
    "type",
    "classification",
    "created",
    "modified",
    "author",
    "field",
  ] as const;
  return (
    <section className="documents-card" aria-labelledby="manifest-guide-title">
      <h2 id="manifest-guide-title">{t("guideTitle")}</h2>
      <p>{t("guideIntro")}</p>
      <dl className="documents-guide">
        {columns.map((c) => (
          <div key={c}>
            <dt>
              <code dir="ltr">{c === "field" ? "field:key" : c}</code>
            </dt>
            <dd>{t(`column.${c}`)}</dd>
          </div>
        ))}
      </dl>
      <p className="documents-meta">{t("guideRules")}</p>
    </section>
  );
}

/** The library's recent imports, newest first, each linking to its report. */
export function ImportList({ imports }: { imports: ImportView[] }) {
  const t = useTranslations("documents.imports");
  const format = useFormatter();
  return (
    <section className="documents-card" aria-labelledby="import-list-title">
      <h2 id="import-list-title">{t("listTitle")}</h2>
      {imports.length === 0 ? (
        <p className="documents-meta">{t("none")}</p>
      ) : (
        <ul className="documents-imports">
          {imports.map((i) => (
            <li key={i.id} data-testid="import">
              <Link href={importHref(i.id)}>
                <bdi>{i.fileName}</bdi>
              </Link>
              <Badge tone={i.status === "completed" ? "success" : "neutral"}>
                {t(`status.${i.status}`)}
              </Badge>
              <span className="documents-meta">
                {t("summary", {
                  imported: i.imported + i.incomplete,
                  total: i.total,
                  problems: i.failed + i.skipped + i.quarantined,
                })}
                {" · "}
                {format.dateTime(new Date(i.createdAt), {
                  dateStyle: "medium",
                  timeStyle: "short",
                })}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/**
 * One import's report: progress while the job runs (the page refreshes itself until it is done), totals per result, and
 * each row with what happened to it, filtered by result, with the whole report as CSV.
 */
export function ImportReport({
  view,
  page,
  status,
}: {
  view: ImportView;
  page: ImportRowPage;
  status: ImportRowState | null;
}) {
  const t = useTranslations("documents.imports");
  const router = useRouter();
  const running = view.status !== "completed";
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => router.refresh(), 4000);
    return () => clearInterval(timer);
  }, [running, router]);
  const counts: Record<ImportRowState, number> = {
    pending: view.pending,
    imported: view.imported,
    incomplete: view.incomplete,
    skipped: view.skipped,
    failed: view.failed,
    quarantined: view.quarantined,
  };
  const filterHref = (s: ImportRowState | null) =>
    s ? `${importHref(view.id)}?status=${s}` : importHref(view.id);
  return (
    <>
      <section
        className="documents-card"
        aria-labelledby="import-progress-title"
      >
        <h2 id="import-progress-title">{t(`status.${view.status}`)}</h2>
        {running ? (
          <Progress
            label={t("progress")}
            value={view.total - view.pending}
            max={view.total}
            valueText={t("progressText", {
              done: view.total - view.pending,
              total: view.total,
            })}
          />
        ) : null}
        <ul className="documents-import-counts" aria-label={t("countsLabel")}>
          <li>
            <Link
              href={filterHref(null)}
              aria-current={status === null ? "page" : undefined}
            >
              {t("all", { count: view.total })}
            </Link>
          </li>
          {importRowStates.map((s) => (
            <li key={s}>
              <Link
                href={filterHref(s)}
                aria-current={status === s ? "page" : undefined}
                data-testid={`count-${s}`}
              >
                {t("count", { result: t(`result.${s}`), count: counts[s] })}
              </Link>
            </li>
          ))}
        </ul>
        <p>
          <a href={`${importApi(view.id)}/report`} download>
            {t("downloadReport")}
          </a>
        </p>
      </section>
      <section className="documents-card" aria-labelledby="import-rows-title">
        <h2 id="import-rows-title">{t("rowsTitle")}</h2>
        {page.rows.length === 0 ? (
          <p className="documents-meta">{t("noRows")}</p>
        ) : (
          <div className="documents-table">
            <table>
              <caption className="visually-hidden">{t("rowsCaption")}</caption>
              <thead>
                <tr>
                  <th scope="col">{t("row")}</th>
                  <th scope="col">{t("file")}</th>
                  <th scope="col">{t("resultLabel")}</th>
                  <th scope="col">{t("details")}</th>
                </tr>
              </thead>
              <tbody>
                {page.rows.map((r) => (
                  <tr key={r.row} data-testid="import-row">
                    <td>{r.row}</td>
                    <td>
                      {r.documentId ? (
                        <Link href={documentHref(r.documentId)}>
                          <bdi>{r.path}</bdi>
                        </Link>
                      ) : (
                        <bdi>{r.path}</bdi>
                      )}
                    </td>
                    <td>
                      <Badge tone={tones[r.status]}>
                        {t(`result.${r.status}`)}
                      </Badge>
                    </td>
                    <td>
                      {r.problemText ? <p>{r.problemText}</p> : null}
                      {r.authorMatched === false ? (
                        <p className="documents-meta">
                          {t("authorNotMatched")}
                        </p>
                      ) : null}
                      {r.versions && r.versions > 1 ? (
                        <p className="documents-meta">
                          {t("versions", { count: r.versions })}
                        </p>
                      ) : null}
                      {r.permissionsDiffer ? (
                        <p
                          className="documents-meta"
                          data-testid="permissions-differ"
                        >
                          {t("permissionsDiffer")}
                        </p>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {page.total > page.rows.length ? (
          <p className="documents-meta">
            {t("moreRows", { shown: page.rows.length, total: page.total })}
          </p>
        ) : null}
      </section>
    </>
  );
}
