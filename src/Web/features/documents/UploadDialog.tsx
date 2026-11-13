"use client";

import { useId, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import {
  nameIn,
  type DocumentView,
  type LibraryView,
} from "@/lib/documents/types";
import { Button } from "@/components/ui/Button";
import { SensitiveWarning } from "@/features/security/SensitiveWarning";
import { readSensitive, type SensitiveProblem } from "@/lib/security/sensitive";
import "./documents.css";

/**
 * The upload dialog (spec 011 US1 scenarios 1 to 3): a file, its type and whether to publish it now. The API checks the
 * file type, size, duplicates, storage and malware, and keeps it a draft while required fields are missing. Sensitive
 * data detection (spec 026 US6) may ask for a reason, or refuse the file until the data is removed.
 */
export function UploadDialog({
  library,
  folderId,
  onUploaded,
}: {
  library: LibraryView;
  folderId: string | null;
  onUploaded: () => void;
}) {
  const t = useTranslations("documents.upload");
  const locale = useLocale();
  const ids = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [type, setType] = useState("");
  const [publish, setPublish] = useState(true);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [done, setDone] = useState<string | null>(null);
  const [sensitive, setSensitive] = useState<SensitiveProblem | null>(null);

  const selected = library.types.find((x) => x.id === type);
  const controlled = Boolean(selected?.controlled);

  async function upload(e: React.FormEvent | null, sensitiveReason?: string) {
    e?.preventDefault();
    if (!file) {
      setMessages([t("noFile")]);
      return;
    }
    setBusy(true);
    setMessages([]);
    setSensitive(null);
    try {
      const q = new URLSearchParams({
        name: file.name,
        publish: String(publish && !controlled),
      });
      if (sensitiveReason) q.set("sensitiveReason", sensitiveReason);
      if (type) q.set("type", type);
      if (folderId) q.set("folder", folderId);
      const response = await fetch(
        `/api/v1/documents/libraries/${encodeURIComponent(library.id)}/files?${q.toString()}`,
        {
          method: "POST",
          headers: { "content-type": "application/octet-stream" },
          body: file,
        },
      );
      if (!response.ok) {
        const found = await readSensitive(response.clone());
        if (found) setSensitive(found);
        else setMessages(await problemMessages(response));
        return;
      }
      const doc = (await response.json()) as DocumentView;
      setDone(
        doc.complete
          ? t("uploaded", { name: doc.name })
          : t("uploadedDraft", { name: doc.name }),
      );
      setFile(null);
      dialog.current?.close();
      onUploaded();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="documents-upload">
      <Button variant="primary" onClick={() => dialog.current?.showModal()}>
        {t("open")}
      </Button>
      {done ? (
        <p className="notice" role="status">
          {done}
        </p>
      ) : null}
      <dialog
        ref={dialog}
        aria-labelledby={`${ids}-title`}
        className="documents-dialog"
      >
        <form onSubmit={(e) => void upload(e)} noValidate>
          <h2 id={`${ids}-title`}>{t("title")}</h2>
          {messages.length > 0 ? (
            <div className="notice notice-error" role="alert">
              {messages.map((m) => (
                <p key={m}>{m}</p>
              ))}
            </div>
          ) : null}
          <div className="field">
            <label htmlFor={`${ids}-file`}>{t("file")}</label>
            <p id={`${ids}-hint`} className="field-hint">
              {t("hint", {
                types: library.effectiveExtensions.join(", "),
                mb: library.effectiveMaxFileMb,
              })}
            </p>
            <input
              id={`${ids}-file`}
              type="file"
              aria-describedby={`${ids}-hint`}
              onChange={(e) => {
                setFile(e.target.files?.[0] ?? null);
                setSensitive(null);
              }}
            />
          </div>
          <div className="field">
            <label htmlFor={`${ids}-type`}>{t("type")}</label>
            <select
              id={`${ids}-type`}
              value={type}
              onChange={(e) => setType(e.target.value)}
            >
              <option value="">{t("noType")}</option>
              {library.types
                .filter((x) => x.status === "active")
                .map((x) => (
                  <option key={x.id} value={x.id}>
                    {nameIn(x.names, locale)}
                  </option>
                ))}
            </select>
          </div>
          {controlled ? (
            <p className="field-hint">{t("controlled")}</p>
          ) : (
            <label className="documents-check">
              <input
                type="checkbox"
                checked={publish}
                onChange={(e) => setPublish(e.target.checked)}
              />
              {t("publish")}
            </label>
          )}
          {sensitive ? <SensitiveWarning problem={sensitive} busy={busy} onConfirm={(reason) => void upload(null, reason)} /> : null}
          <div className="documents-actions">
            <Button
              type="submit"
              variant="primary"
              busy={busy}
              busyLabel={t("busy")}
            >
              {t("submit")}
            </Button>
            <Button onClick={() => dialog.current?.close()}>
              {t("cancel")}
            </Button>
          </div>
        </form>
      </dialog>
    </div>
  );
}
