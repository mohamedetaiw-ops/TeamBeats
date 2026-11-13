"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import { complianceApi, libraryHref, pick, type FrameworkSummary, type LibraryView } from "@/lib/compliance";

/** One control per line: ID, then the English title, then the Arabic title, separated by a vertical bar. */
function parseControls(text: string) {
  return text
    .split("\n")
    .map((line) => line.split("|").map((part) => part.trim()))
    .filter((parts) => parts[0])
    .map(([code, en, ar]) => ({ code, titles: Object.fromEntries(Object.entries({ en, ar }).filter(([, v]) => v)) }));
}

/**
 * The control library (FR-021): the platform sets (ISO 27001:2022 Annex A, SOC 2, NCA ECC) and the organization's own
 * frameworks. Compliance officers for the whole organization add frameworks; projects already started keep their copy.
 */
export function FrameworkAdmin({ library }: { library: LibraryView }) {
  const t = useTranslations("compliance.library");
  const locale = useLocale();
  const router = useRouter();
  const action = useAdminAction(libraryHref);
  const [key, setKey] = useState("");
  const [version, setVersion] = useState("");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [controls, setControls] = useState("");

  async function add(e: FormEvent) {
    e.preventDefault();
    const names = Object.fromEntries(Object.entries({ en: nameEn, ar: nameAr }).filter(([, v]) => v.trim()));
    const outcome = await action.run("add", "POST", `${complianceApi}/library`, { key, version, names, controls: parseControls(controls) }, t("added"));
    if (outcome.ok) {
      setKey("");
      setVersion("");
      setNameEn("");
      setNameAr("");
      setControls("");
      router.refresh();
    }
  }

  return (
    <div className="compliance">
      <ResultAlert result={action.result} />
      <DataTable<FrameworkSummary>
        caption={t("title")}
        rows={library.frameworks}
        rowKey={(f) => f.key}
        columns={[
          { key: "name", header: t("name"), rowHeader: true, cell: (f) => <bdi>{pick(f.names, locale)}</bdi> },
          { key: "version", header: t("version"), cell: (f) => <bdi dir="ltr">{f.version}</bdi> },
          { key: "controls", header: t("controls"), align: "end", cell: (f) => f.controlCount },
          { key: "source", header: t("source"), cell: (f) => <Badge tone={f.platform ? "info" : "accent"}>{t(f.platform ? "platform" : "own")}</Badge> },
        ]}
      />
      {library.canManage ? (
        <form className="compliance-section" onSubmit={(e) => void add(e)} aria-labelledby="library-add">
          <h2 id="library-add">{t("addTitle")}</h2>
          <p className="compliance-meta">{t("addIntro")}</p>
          <div className="admin-grid">
            <FormField label={t("key")} hint={t("keyHint")} name="key" dir="ltr" required maxLength={40} value={key} onChange={(e) => setKey(e.target.value)} />
            <FormField label={t("version")} name="version" required maxLength={40} value={version} onChange={(e) => setVersion(e.target.value)} />
            <FormField label={t("nameEn")} name="nameEn" lang="en" dir="ltr" maxLength={120} value={nameEn} onChange={(e) => setNameEn(e.target.value)} />
            <FormField label={t("nameAr")} name="nameAr" lang="ar" dir="rtl" maxLength={120} value={nameAr} onChange={(e) => setNameAr(e.target.value)} />
          </div>
          <TextAreaField label={t("controlLines")} hint={t("controlLinesHint")} name="controls" rows={8} value={controls} onChange={(e) => setControls(e.target.value)} />
          <div className="admin-row">
            <Button type="submit" variant="primary" icon="plus" busy={action.busy === "add"} busyLabel={t("working")}>
              {t("add")}
            </Button>
          </div>
        </form>
      ) : null}
    </div>
  );
}
