"use client";

import { useId, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import {
  nameIn,
  type DocumentType,
  type DocumentView,
  type PreviewView,
  type TokenView,
  type VersionView,
} from "@/lib/documents/types";
import { Button } from "@/components/ui/Button";
import "./documents.css";

const base = (id: string) => `/api/v1/documents/${encodeURIComponent(id)}`;

type Preview =
  | "closed"
  | "loading"
  | "failed"
  | { view: PreviewView; text?: string };

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return "";
  if (
    typeof value === "string" ||
    typeof value === "number" ||
    typeof value === "boolean"
  )
    return String(value);
  return JSON.stringify(value);
}

/**
 * The document page body (spec 011): metadata, controlled state, preview, download, check-out and check-in, release
 * and read confirmation, and the version history. Every button reflects what the API returned for this person; the
 * API decides and refuses, the page only shows the answer.
 */
export function DocumentPanel({
  initial,
  versions: initialVersions,
  types,
  me,
}: {
  initial: DocumentView;
  versions: VersionView[];
  types: DocumentType[];
  me: string;
}) {
  const t = useTranslations("documents.document");
  const locale = useLocale();
  const format = useFormatter();
  const ids = useId();
  const [doc, setDoc] = useState(initial);
  const [versions, setVersions] = useState(initialVersions);
  const [messages, setMessages] = useState<string[]>([]);
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [preview, setPreview] = useState<Preview>("closed");
  const [file, setFile] = useState<File | null>(null);
  const [note, setNote] = useState("");
  const [major, setMajor] = useState(false);
  const [effectiveOn, setEffectiveOn] = useState("");

  const type = types.find((x) => x.id === doc.typeId) ?? null;
  const controlled = doc.controlled ?? null;
  const mine = doc.checkedOutBy === me;
  const date = (iso: string) =>
    format.dateTime(new Date(iso), { dateStyle: "medium" });

  async function run(
    key: string,
    call: () => Promise<Response>,
    done?: string,
  ) {
    setBusy(key);
    setMessages([]);
    setStatus(null);
    try {
      const response = await call();
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return null;
      }
      if (done) {
        // Actions answer with the document as it now is; the version list is read again after them.
        setDoc((await response.json()) as DocumentView);
        const list = await fetch(`${base(doc.id)}/versions`);
        if (list.ok) setVersions((await list.json()) as VersionView[]);
        setStatus(done);
      }
      return response;
    } catch {
      setMessages([t("failed")]);
      return null;
    } finally {
      setBusy(null);
    }
  }

  const post = (path: string, body?: unknown) =>
    fetch(`${base(doc.id)}${path}`, {
      method: "POST",
      headers:
        body === undefined ? undefined : { "content-type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });

  async function download(version?: string) {
    const response = await run("download", () =>
      fetch(
        `${base(doc.id)}/download-token${version ? `?version=${encodeURIComponent(version)}` : ""}`,
      ),
    );
    if (response)
      window.location.assign(((await response.json()) as TokenView).url);
  }

  async function controlledCopy() {
    const response = await run("copy", () =>
      fetch(`${base(doc.id)}/controlled-copy`),
    );
    if (response)
      window.location.assign(((await response.json()) as TokenView).url);
  }

  async function showPreview() {
    setPreview("loading");
    try {
      const response = await fetch(`${base(doc.id)}/preview`);
      if (!response.ok) throw new Error(String(response.status));
      const view = (await response.json()) as PreviewView;
      if (view.kind === "text" && view.url) {
        const text = await (await fetch(view.url)).text();
        setPreview({ view, text });
      } else {
        setPreview({ view });
      }
    } catch {
      setPreview("failed");
    }
  }

  function checkIn(e: React.FormEvent) {
    e.preventDefault();
    const q = new URLSearchParams({ publish: String(major && !controlled) });
    if (note.trim()) q.set("note", note.trim());
    if (file) q.set("name", file.name);
    void run(
      "checkin",
      () =>
        fetch(`${base(doc.id)}/checkin?${q.toString()}`, {
          method: "POST",
          headers: file
            ? { "content-type": "application/octet-stream" }
            : undefined,
          body: file ?? undefined,
        }),
      t("checkedInDone"),
    ).then((r) => {
      if (r) {
        setFile(null);
        setNote("");
      }
    });
  }

  function release(e: React.FormEvent) {
    e.preventDefault();
    void run(
      "release",
      () =>
        post("/release", {
          effectiveOn: effectiveOn || null,
          note: note.trim() || null,
        }),
      t("releaseSent"),
    );
  }

  return (
    <article className="documents-document">
      <h1>
        <bdi>{doc.name}</bdi>
      </h1>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {status ? (
        <p className="notice" role="status">
          {status}
        </p>
      ) : null}

      <dl className="documents-facts">
        {doc.number ? (
          <>
            <dt>{t("number")}</dt>
            <dd data-testid="document-number">
              <bdi>{doc.number}</bdi>
            </dd>
          </>
        ) : null}
        <dt>{t("type")}</dt>
        <dd>{type ? nameIn(type.names, locale) : t("noType")}</dd>
        <dt>{t("classification")}</dt>
        <dd>{t(`classificationValue.${doc.classification}`)}</dd>
        <dt>{t("version")}</dt>
        <dd data-testid="document-version">
          {(doc.effective ?? doc.current)?.label ?? t("noVersion")}
        </dd>
        {(type?.fields ?? []).map((f) => (
          <div key={f.key} className="documents-fact">
            <dt>{nameIn(f.names, locale)}</dt>
            <dd>
              <bdi>{formatValue(doc.metadata[f.key])}</bdi>
            </dd>
          </div>
        ))}
        {controlled?.reviewDueAt ? (
          <>
            <dt>{t("reviewDue")}</dt>
            <dd>{date(controlled.reviewDueAt)}</dd>
          </>
        ) : null}
      </dl>

      {!doc.complete ? <p className="notice">{t("incomplete")}</p> : null}
      {doc.checkedOutBy ? (
        <p className="notice">
          {mine ? t("checkedOutByYou") : t("checkedOutByOther")}
        </p>
      ) : null}
      {controlled?.release === "in_approval" ? (
        <p className="notice">{t("inApproval")}</p>
      ) : null}
      {controlled?.release === "scheduled" && controlled.upcoming ? (
        <p className="notice" data-testid="document-upcoming">
          {t("upcoming", { version: controlled.upcoming.label })}
        </p>
      ) : null}

      {doc.downloadable === false ? (
        <p className="notice" data-testid="view-only-notice">
          {t("viewOnlyNotice")}
        </p>
      ) : null}

      <div className="documents-actions">
        {(doc.effective || doc.current) && doc.downloadable !== false ? (
          <Button
            variant="primary"
            busy={busy === "download"}
            busyLabel={t("busy")}
            onClick={() => void download()}
          >
            {t("download")}
          </Button>
        ) : null}
        <Button onClick={() => void showPreview()}>{t("preview")}</Button>
        {controlled && doc.effective && doc.downloadable !== false ? (
          <Button
            busy={busy === "copy"}
            busyLabel={t("busy")}
            onClick={() => void controlledCopy()}
          >
            {t("controlledCopy")}
          </Button>
        ) : null}
        {controlled?.readConfirmationRequired && doc.effective ? (
          controlled.readConfirmedAt ? (
            <p className="documents-meta" data-testid="read-confirmed">
              {t("confirmed", { date: date(controlled.readConfirmedAt) })}
            </p>
          ) : (
            <Button
              busy={busy === "confirm"}
              busyLabel={t("busy")}
              onClick={() =>
                void run(
                  "confirm",
                  () => post("/read-confirmation"),
                  t("confirmedDone"),
                )
              }
            >
              {t("confirmRead")}
            </Button>
          )
        ) : null}
        {doc.rights.contribute &&
        doc.checkedIn &&
        !doc.checkedOutBy &&
        controlled?.release !== "in_approval" ? (
          <Button
            busy={busy === "checkout"}
            busyLabel={t("busy")}
            onClick={() =>
              void run("checkout", () => post("/checkout"), t("checkedOutDone"))
            }
          >
            {t("checkOut")}
          </Button>
        ) : null}
        {controlled?.release === "in_approval" && doc.rights.contribute ? (
          <Button
            busy={busy === "withdraw"}
            busyLabel={t("busy")}
            onClick={() =>
              void run(
                "withdraw",
                () => post("/release/withdraw"),
                t("withdrawn"),
              )
            }
          >
            {t("withdrawRelease")}
          </Button>
        ) : null}
      </div>

      {preview !== "closed" ? (
        <section
          className="documents-preview"
          aria-label={t("preview")}
          aria-busy={preview === "loading" || undefined}
        >
          {preview === "loading" ? (
            <p>{t("previewLoading")}</p>
          ) : preview === "failed" ? (
            <p>{t("previewFailed")}</p>
          ) : (
            <div
              className={
                preview.view.watermark
                  ? "documents-preview-frame documents-watermarked"
                  : "documents-preview-frame"
              }
              onContextMenu={
                preview.view.watermark ? (e) => e.preventDefault() : undefined
              }
            >
              {preview.view.kind === "pdf" && preview.view.url ? (
                <iframe src={preview.view.url} title={t("preview")} />
              ) : preview.view.kind === "image" && preview.view.url ? (
                // eslint-disable-next-line @next/next/no-img-element -- previews are short-lived tokens the image optimizer cannot fetch
                <img src={preview.view.url} alt={doc.name} draggable={false} />
              ) : preview.view.kind === "video" && preview.view.url ? (
                <video
                  src={preview.view.url}
                  controls
                  controlsList={
                    preview.view.watermark ? "nodownload" : undefined
                  }
                />
              ) : preview.view.kind === "text" ? (
                <pre dir="auto">{preview.text}</pre>
              ) : (
                <p data-testid="preview-unavailable">
                  {t("previewUnavailable")}
                </p>
              )}
              {preview.view.watermark ? (
                <p className="documents-watermark" data-testid="watermark">
                  <bdi>{preview.view.watermark}</bdi>
                </p>
              ) : null}
            </div>
          )}
        </section>
      ) : null}

      {mine ? (
        <form className="documents-card" onSubmit={checkIn} noValidate>
          <h2>{t("checkInTitle")}</h2>
          <div className="field">
            <label htmlFor={`${ids}-file`}>{t("newFile")}</label>
            <input
              id={`${ids}-file`}
              type="file"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </div>
          <div className="field">
            <label htmlFor={`${ids}-note`}>{t("note")}</label>
            <input
              id={`${ids}-note`}
              value={note}
              onChange={(e) => setNote(e.target.value)}
              maxLength={500}
            />
          </div>
          {controlled ? null : (
            <label className="documents-check">
              <input
                type="checkbox"
                checked={major}
                onChange={(e) => setMajor(e.target.checked)}
              />
              {t("major")}
            </label>
          )}
          <div className="documents-actions">
            <Button
              type="submit"
              variant="primary"
              busy={busy === "checkin"}
              busyLabel={t("busy")}
            >
              {t("checkIn")}
            </Button>
            <Button
              variant="destructive"
              busy={busy === "discard"}
              busyLabel={t("busy")}
              onClick={() =>
                void run("discard", () => post("/discard"), t("discarded"))
              }
            >
              {t("discard")}
            </Button>
          </div>
        </form>
      ) : null}

      {controlled &&
      doc.rights.contribute &&
      doc.checkedIn &&
      !doc.checkedOutBy &&
      controlled.release === "none" &&
      doc.current &&
      doc.current.minor > 0 ? (
        <form className="documents-card" onSubmit={release} noValidate>
          <h2>{t("releaseTitle")}</h2>
          <div className="field">
            <label htmlFor={`${ids}-effective`}>{t("effectiveOn")}</label>
            <input
              id={`${ids}-effective`}
              type="date"
              value={effectiveOn}
              onChange={(e) => setEffectiveOn(e.target.value)}
            />
          </div>
          <div className="field">
            <label htmlFor={`${ids}-release-note`}>{t("note")}</label>
            <input
              id={`${ids}-release-note`}
              value={note}
              onChange={(e) => setNote(e.target.value)}
              maxLength={500}
            />
          </div>
          <Button
            type="submit"
            variant="primary"
            busy={busy === "release"}
            busyLabel={t("busy")}
          >
            {t("submitRelease")}
          </Button>
        </form>
      ) : null}

      <section className="documents-card" aria-labelledby={`${ids}-versions`}>
        <h2 id={`${ids}-versions`}>{t("versions")}</h2>
        {versions.length === 0 ? (
          <p>{t("noVersions")}</p>
        ) : (
          <div className="documents-table">
            <table data-testid="version-list">
              <thead>
                <tr>
                  <th scope="col">{t("version")}</th>
                  <th scope="col">{t("state")}</th>
                  <th scope="col">{t("created")}</th>
                  <th scope="col">{t("note")}</th>
                  <th scope="col">
                    <span className="visually-hidden">{t("actions")}</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {versions.map((v) => (
                  <tr key={v.id}>
                    <td>{v.label}</td>
                    <td>{t(`stateValue.${v.state}`)}</td>
                    <td>
                      <time dateTime={v.createdAt}>{date(v.createdAt)}</time>
                    </td>
                    <td>
                      <bdi>{v.note ?? ""}</bdi>
                    </td>
                    <td className="documents-row-actions">
                      {doc.downloadable !== false ? (
                        <Button
                          variant="quiet"
                          onClick={() => void download(v.id)}
                        >
                          {t("downloadVersion", { version: v.label })}
                        </Button>
                      ) : null}
                      {doc.rights.contribute &&
                      doc.checkedIn &&
                      !doc.checkedOutBy &&
                      v.id !== (doc.current?.id ?? "") ? (
                        <Button
                          variant="quiet"
                          busy={busy === `restore-${v.id}`}
                          busyLabel={t("busy")}
                          onClick={() =>
                            void run(
                              `restore-${v.id}`,
                              () =>
                                post("/restore", {
                                  versionId: v.id,
                                  note: null,
                                }),
                              t("restored"),
                            )
                          }
                        >
                          {t("restore", { version: v.label })}
                        </Button>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </article>
  );
}
