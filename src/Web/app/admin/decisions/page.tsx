import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick } from "@/lib/services/types";
import type { DecisionSummary } from "@/lib/processes";
import { DecisionEditor } from "@/features/processes/DecisionEditor";
import "@/features/processes/processes.css";

/**
 * Decisions (spec 024 FR-007): the decision tables the person may design or read, and a new table. The section
 * appears only when the API lists it; every call is authorized again by the API.
 */
export default async function DecisionsPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "decisions")) {
    notFound();
  }
  const [decisions, units] = await Promise.all([apiGet<DecisionSummary[]>("/api/v1/workflow/decisions"), listUnits()]);
  const t = await getTranslations("processes.decisions");
  const locale = await getLocale();
  return (
    <>
      <h1>{t("pageTitle")}</h1>
      <p className="notice">{t("pageIntro")}</p>
      <section className="processes-section" aria-labelledby="decision-list">
        <h2 id="decision-list">{t("listTitle")}</h2>
        {decisions.ok && decisions.data.length > 0 ? (
          <ul className="processes-list">
            {decisions.data.map((d) => (
              <li key={d.key}>
                <Link href={`/admin/decisions/${encodeURIComponent(d.key)}`}>
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
          <p className="field-hint">{t("empty")}</p>
        )}
      </section>
      <h2>{t("newTitle")}</h2>
      <DecisionEditor decisionKey={null} initial={null} units={units.ok ? units.data : []} />
    </>
  );
}
