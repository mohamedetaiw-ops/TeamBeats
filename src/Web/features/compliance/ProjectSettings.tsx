"use client";

import { useEffect, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import { complianceApi, projectHref, type ProjectSummary, type ProjectView } from "@/lib/compliance";

type OwnedSpace = { id: string; name: string; role: string | null; status: string };

/**
 * A manager's settings for a project: its name, archive or restore, and sharing it read-only to a space they own, such
 * as the guest space of an external audit (FR-024). The API checks ownership of the space and audits each change.
 */
export function ProjectSettings({ project, version }: { project: ProjectSummary; version: number }) {
  const t = useTranslations("compliance.settings");
  const router = useRouter();
  const action = useAdminAction(projectHref(project.id));
  const [name, setName] = useState(project.name);
  const [space, setSpace] = useState(project.sharedSpace?.spaceId ?? "");
  const [spaces, setSpaces] = useState<OwnedSpace[]>([]);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/spaces?mine=true", { cache: "no-store" });
        if (live && response.ok) setSpaces(((await response.json()) as OwnedSpace[]).filter((s) => s.role === "owner" && s.status === "active"));
      } catch {
        // Without the list the project can still be renamed or archived.
      }
    })();
    return () => {
      live = false;
    };
  }, []);

  async function rename(e: FormEvent) {
    e.preventDefault();
    const outcome = await action.run<ProjectView>("rename", "PUT", `${complianceApi}/projects/${project.id}`, { name, version }, t("saved"));
    if (outcome.ok) router.refresh();
  }

  async function archive() {
    const status = project.status === "active" ? "archived" : "active";
    const outcome = await action.run<ProjectView>("archive", "PUT", `${complianceApi}/projects/${project.id}`, { status, version }, t(status === "archived" ? "archived" : "restored"));
    if (outcome.ok) router.refresh();
  }

  async function share(e: FormEvent) {
    e.preventDefault();
    const outcome = await action.run<ProjectView>("share", "PUT", `${complianceApi}/projects/${project.id}/share`, { spaceId: space || null }, t(space ? "shared" : "unshared"));
    if (outcome.ok) router.refresh();
  }

  const options = spaces.some((s) => s.id === project.sharedSpace?.spaceId) || !project.sharedSpace
    ? spaces
    : [...spaces, { id: project.sharedSpace.spaceId, name: project.sharedSpace.name, role: "owner", status: "active" }];

  return (
    <section className="compliance-section" aria-labelledby="compliance-settings">
      <h2 id="compliance-settings">{t("title")}</h2>
      <ResultAlert result={action.result} />
      <form className="ui-control-row" onSubmit={(e) => void rename(e)}>
        <FormField label={t("name")} name="name" required maxLength={120} value={name} onChange={(e) => setName(e.target.value)} />
        <Button type="submit" busy={action.busy === "rename"} busyLabel={t("working")}>
          {t("rename")}
        </Button>
      </form>
      <form className="ui-control-row" onSubmit={(e) => void share(e)}>
        <SelectField
          label={t("space")}
          hint={t("spaceHint")}
          name="space"
          value={space}
          disabled={project.status !== "active"}
          onChange={(e) => setSpace(e.target.value)}
          options={[{ value: "", label: t("notShared") }, ...options.map((s) => ({ value: s.id, label: s.name }))]}
        />
        <Button type="submit" busy={action.busy === "share"} busyLabel={t("working")} disabled={project.status !== "active"}>
          {t("share")}
        </Button>
      </form>
      <div className="ui-control-row">
        <Button busy={action.busy === "archive"} busyLabel={t("working")} icon={project.status === "active" ? "lock" : "check"} onClick={() => void archive()}>
          {t(project.status === "active" ? "archive" : "restore")}
        </Button>
      </div>
    </section>
  );
}
