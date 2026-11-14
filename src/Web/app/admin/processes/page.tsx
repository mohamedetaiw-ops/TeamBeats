import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick } from "@/lib/services/types";
import type { ProcessModelSummary } from "@/lib/processes";
import { Modeler } from "@/features/processes/Modeler";
import "@/features/processes/processes.css";
import { StateView } from "@/components/ui/StateView";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Processes (spec 024 FR-001): the process models the person may design or read, and a new model. The section
 * appears only when the API lists it; every call is authorized again by the API.
 */
export default async function ProcessesPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "processes")) {
    notFound();
  }
  const [models, units] = await Promise.all([apiGet<ProcessModelSummary[]>("/api/v1/workflow/processes"), listUnits()]);
  const t = await getTranslations("processes.modeler");
  const locale = await getLocale();
  return (
    <>
      <AdminHeader section="processes" description={t("pageIntro")} />
      <section className="processes-section" aria-labelledby="process-list">
        <h2 id="process-list">{t("listTitle")}</h2>
        {models.ok && models.data.length > 0 ? (
          <ul className="processes-list">
            {models.data.map((d) => (
              <li key={d.key}>
                <Link href={`/admin/processes/${encodeURIComponent(d.key)}`}>
                  <bdi>{pick(d.names, locale, d.key)}</bdi>
                </Link>{" "}
                <span className="field-hint">
                  {d.latestVersion == null ? t("notPublished") : t("latestVersion", { version: d.latestVersion })}
                  {d.problems > 0 ? ` · ${t("problemCount", { count: d.problems })}` : ""}
                </span>
              </li>
            ))}
          </ul>
        ) : (
          <StateView kind="empty" title={t("empty")} />
        )}
      </section>
      <Modeler processKey={null} initial={null} units={units.ok ? units.data : []} />
    </>
  );
}
