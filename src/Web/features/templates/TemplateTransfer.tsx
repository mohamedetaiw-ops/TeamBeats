"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { FormField } from "@/components/ui/FormField";
import { SendMessages, useSend } from "@/features/builder/manage/useSend";

/**
 * Template export (spec 032 R2 T028, FR-022): downloads the template as the documented JSON format
 * (docs/api/template-export.v1.md), so it can be imported into another organization.
 */
export function ExportTemplate({ templateId }: { templateId: string }) {
  const t = useTranslations("templates.transfer");
  const { busy, result, setResult } = useSend(t("failed"));
  const [working, setWorking] = useState(false);

  async function download() {
    setWorking(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/experience/templates/${encodeURIComponent(templateId)}/export`, { cache: "no-store" });
      if (!response.ok) {
        setResult({ ok: false, messages: [t("failed")] });
        return;
      }
      const url = URL.createObjectURL(new Blob([await response.text()], { type: "application/json" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `${templateId}.template.json`;
      link.click();
      URL.revokeObjectURL(url);
      setResult({ ok: true, messages: [t("exported")] });
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setWorking(false);
    }
  }

  return (
    <div className="builder-manage-inline" data-testid="template-export">
      <Button variant="secondary" busy={busy || working} busyLabel={t("working")} onClick={() => void download()}>
        {t("export")}
      </Button>
      <SendMessages result={result} />
    </div>
  );
}

/**
 * Template import (spec 032 R2 T028): reads an exported file and creates a new template from it under the id chosen here.
 * The API checks the format, the base template and every region rule; this form only sends the file.
 */
export function ImportTemplate() {
  const t = useTranslations("templates.transfer");
  const router = useRouter();
  const [file, setFile] = useState<{ item: UploadItem; text: string } | null>(null);
  const [id, setId] = useState("");
  const [readError, setReadError] = useState<string | null>(null);
  const { busy, result, send } = useSend(t("failed"));

  async function add(files: File[]) {
    const chosen = files[0];
    if (!chosen) return;
    setReadError(null);
    setFile({ item: { key: chosen.name, name: chosen.name, size: chosen.size }, text: await chosen.text() });
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!file) {
      setReadError(t("noFile"));
      return;
    }
    let document: unknown;
    try {
      document = JSON.parse(file.text);
    } catch {
      setReadError(t("notJson"));
      return;
    }
    const made = await send<{ id: string }>("/api/v1/experience/templates/import", "POST", { document, id }, t("imported"));
    if (made?.id) router.push(`/admin/templates/${made.id}`);
  }

  return (
    <form className="builder-manage-form" onSubmit={(e) => void submit(e)} aria-labelledby="template-import-title" data-testid="template-import">
      <h2 id="template-import-title">{t("importTitle")}</h2>
      <p className="field-hint">{t("importHint")}</p>
      <FileUpload
        label={t("file")}
        hint={t("fileHint")}
        accept="application/json,.json"
        multiple={false}
        items={file ? [file.item] : []}
        onAdd={(files) => void add(files)}
        onRemove={() => setFile(null)}
        error={readError}
      />
      <FormField label={t("id")} name="template-import-id" value={id} hint={t("idHint")} dir="ltr" maxLength={40} onChange={(e) => setId(e.target.value)} required />
      <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
        {t("import")}
      </Button>
      <SendMessages result={result} />
    </form>
  );
}
