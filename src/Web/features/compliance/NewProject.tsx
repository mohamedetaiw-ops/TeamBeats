"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import type { OrgUnitView } from "@/lib/admin/types";
import { complianceApi, complianceHref, pick, projectHref, type FrameworkSummary, type ProjectView } from "@/lib/compliance";

/**
 * Starts a project from a framework of the control library (US7 scenario 1), for the whole organization or one unit
 * and the units below it. The API checks that the person manages compliance there and copies every control.
 */
export function NewProject({ frameworks }: { frameworks: FrameworkSummary[] }) {
  const t = useTranslations("compliance.create");
  const locale = useLocale();
  const router = useRouter();
  const action = useAdminAction(complianceHref);
  const [name, setName] = useState("");
  const [framework, setFramework] = useState(frameworks[0]?.key ?? "");
  const [unit, setUnit] = useState("");
  const [units, setUnits] = useState<OrgUnitView[]>([]);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/org/units", { cache: "no-store" });
        if (live && response.ok) setUnits(((await response.json()) as OrgUnitView[]).filter((u) => u.status === "active"));
      } catch {
        // Without the unit list the project is for the whole organization.
      }
    })();
    return () => {
      live = false;
    };
  }, []);

  async function create(e: FormEvent) {
    e.preventDefault();
    const outcome = await action.run<ProjectView>("create", "POST", `${complianceApi}/projects`, { name, frameworkKey: framework, orgUnitId: unit || null }, t("created"));
    if (outcome.ok) router.push(projectHref(outcome.data.project.id));
  }

  return (
    <form className="compliance-section" onSubmit={(e) => void create(e)} aria-labelledby="compliance-new">
      <h2 id="compliance-new">{t("title")}</h2>
      <ResultAlert result={action.result} />
      <div className="admin-grid">
        <FormField label={t("name")} hint={t("nameHint")} name="name" required maxLength={120} value={name} onChange={(e) => setName(e.target.value)} />
        <SelectField
          label={t("framework")}
          name="framework"
          value={framework}
          onChange={(e) => setFramework(e.target.value)}
          options={frameworks.map((f) => ({ value: f.key, label: `${pick(f.names, locale)} (${f.version})` }))}
        />
        <SelectField
          label={t("scope")}
          hint={t("scopeHint")}
          name="scope"
          value={unit}
          onChange={(e) => setUnit(e.target.value)}
          options={[{ value: "", label: t("wholeTenant") }, ...units.map((u) => ({ value: u.id, label: pick(u.names, locale) }))]}
        />
      </div>
      <div className="admin-row">
        <Button type="submit" variant="primary" icon="plus" busy={action.busy === "create"} busyLabel={t("working")}>
          {t("submit")}
        </Button>
      </div>
    </form>
  );
}
