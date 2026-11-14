"use client";

import { useId, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { Icon } from "@/components/ui/Icon";
import { IconButton } from "@/components/ui/IconButton";
import { problemMessages } from "@/lib/notifications/types";
import { FILE_CLASSIFICATIONS, type ConversationFile } from "@/lib/assistant/types";

/**
 * Files in a conversation (spec 014 US5): the person picks a file and its classification, Internal unless they
 * choose. The API scans it, reads its text, refuses it with the reason when the AI policy does not allow its
 * classification, and keeps only the text for this conversation. Shown only when the API offers files.
 */
export function ConversationFiles({
  conversationId,
  files,
  onChange,
  disabled = false,
}: {
  conversationId: string | null;
  files: readonly ConversationFile[];
  onChange: (files: ConversationFile[]) => void;
  disabled?: boolean;
}) {
  const t = useTranslations("assistant.files");
  const [classification, setClassification] = useState<string>("internal");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const picker = useRef<HTMLInputElement>(null);
  const id = useId();

  async function upload(file: File) {
    setBusy(true);
    setError(null);
    try {
      const query = new URLSearchParams({ name: file.name, classification });
      if (conversationId) query.set("conversationId", conversationId);
      const response = await fetch(`/api/v1/assistant/files?${query.toString()}`, {
        method: "POST",
        headers: { "content-type": file.type || "application/octet-stream" },
        body: file,
      });
      if (!response.ok) {
        setError((await problemMessages(response)).join(" ") || t("failed"));
        return;
      }
      onChange([...files, (await response.json()) as ConversationFile]);
    } catch {
      setError(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  async function remove(file: ConversationFile) {
    setError(null);
    const response = await fetch(`/api/v1/assistant/files/${encodeURIComponent(file.id)}`, { method: "DELETE" }).catch(() => null);
    if (!response?.ok) {
      setError(t("failed"));
      return;
    }
    onChange(files.filter((f) => f.id !== file.id));
  }

  return (
    <div className="assistant-files">
      {files.length ? (
        <ul className="ui-composer-files" aria-label={t("list")}>
          {files.map((f) => (
            <li key={f.id} className="ui-composer-file">
              <Icon name="files" size={16} />
              <span className="ui-composer-file-name">
                <bdi>{f.name}</bdi>
                <span>{t(`classifications.${f.classification}`)}</span>
              </span>
              <IconButton icon="close" size="xs" label={t("remove", { name: f.name })} onClick={() => void remove(f)} />
            </li>
          ))}
        </ul>
      ) : null}
      <div className="assistant-files-bar">
        <label className="ui-toolbar-select" htmlFor={`${id}-class`}>
          <span>{t("classification")}</span>
          <select id={`${id}-class`} value={classification} disabled={disabled || busy} onChange={(e) => setClassification(e.target.value)}>
            {FILE_CLASSIFICATIONS.map((c) => (
              <option key={c} value={c}>
                {t(`classifications.${c}`)}
              </option>
            ))}
          </select>
        </label>
        <IconButton icon="attach" size="sm" label={busy ? t("uploading") : t("attach")} disabled={disabled || busy} onClick={() => picker.current?.click()} />
        <input
          ref={picker}
          type="file"
          accept=".pdf,.docx,.xlsx,.pptx,.odt,.txt,.md,.csv,.png,.jpg,.jpeg"
          hidden
          tabIndex={-1}
          aria-describedby={`${id}-hint`}
          onChange={(e) => {
            const chosen = e.currentTarget.files?.[0];
            e.currentTarget.value = "";
            if (chosen) void upload(chosen);
          }}
        />
        <span id={`${id}-hint`} className="field-hint">
          {t("hint")}
        </span>
      </div>
      <div aria-live="polite">
        {busy ? (
          <p className="field-hint" role="status">
            {t("uploading")}
          </p>
        ) : null}
        {error ? (
          <p className="field-error" role="alert">
            {error}
          </p>
        ) : null}
      </div>
    </div>
  );
}
