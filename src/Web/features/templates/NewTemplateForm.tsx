"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import type { ProblemErrors } from "@/lib/brand/types";

type Base = { id: string; name: string; description: string };

/** Starts a tenant template as a copy of a platform template. The API checks the ID, the names and the plan limit. */
export function NewTemplateForm({ bases }: { bases: Base[] }) {
  const t = useTranslations("templates");
  const router = useRouter();
  const [id, setId] = useState("");
  const [basedOn, setBasedOn] = useState(bases[0]?.id ?? "");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [errors, setErrors] = useState<ProblemErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const fieldError = (field: string) => errors[field]?.[0]?.message;

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setErrors({});
    setFormError(null);
    const names: Record<string, string> = {};
    if (nameEn.trim()) names.en = nameEn;
    if (nameAr.trim()) names.ar = nameAr;
    try {
      const response = await fetch("/api/v1/experience/templates", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ id, basedOn, names }),
      });
      if (response.ok) {
        router.push(`/admin/templates/${encodeURIComponent(id)}`);
        return;
      }
      const problem = (await response.json().catch(() => null)) as { errors?: ProblemErrors; title?: string } | null;
      if (response.status === 400) setErrors(problem?.errors ?? {});
      else if (response.status === 409) setErrors({ id: [{ key: "conflict.template.id_taken", message: t("idTaken") }] });
      else setFormError(problem?.title ?? t("failed"));
    } catch {
      setFormError(t("failed"));
    } finally {
      setBusy(false);
    }
  }

  const chosen = bases.find((b) => b.id === basedOn);
  return (
    <form className="builder-create" onSubmit={(e) => void submit(e)} noValidate aria-labelledby="new-template-title">
      <h2 id="new-template-title">{t("create")}</h2>
      <div className="field">
        <label htmlFor="template-base">{t("basedOn")}</label>
        <select id="template-base" value={basedOn} onChange={(e) => setBasedOn(e.target.value)} aria-describedby="template-base-description">
          {bases.map((b) => (
            <option key={b.id} value={b.id}>
              {b.name}
            </option>
          ))}
        </select>
        <p id="template-base-description" className="field-hint">
          {chosen?.description}
        </p>
      </div>
      <FormField label={t("id")} name="id" hint={t("idHint")} value={id} onChange={(e) => setId(e.target.value)} error={fieldError("id")} dir="ltr" />
      <FormField label={t("nameEn")} name="nameEn" lang="en" dir="ltr" value={nameEn} onChange={(e) => setNameEn(e.target.value)} error={fieldError("names")} />
      <FormField label={t("nameAr")} name="nameAr" lang="ar" dir="rtl" value={nameAr} onChange={(e) => setNameAr(e.target.value)} />
      {formError ? (
        <p className="field-error" role="alert">
          {formError}
        </p>
      ) : null}
      <Button type="submit" variant="primary" busy={busy} busyLabel={t("creating")}>
        {t("createAction")}
      </Button>
    </form>
  );
}
