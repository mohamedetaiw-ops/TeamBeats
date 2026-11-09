import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { DatasetView, Persona } from "@/lib/evaluation/types";
import { DatasetEditor } from "@/features/evaluation/DatasetEditor";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";

/** A golden dataset (spec 034 US2): its cases, versions and import. Older versions open read only, as evidence. */
export default async function EvaluationDatasetPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ version?: string }> }) {
  await evaluationSection();
  const { id } = await params;
  const { version } = await searchParams;
  const pinned = version && /^\d{1,6}$/.test(version) ? version : null;
  const [dataset, personas] = await Promise.all([
    apiGet<DatasetView>(`/api/v1/evaluation/datasets/${encodeURIComponent(id)}${pinned ? `?version=${pinned}` : ""}`),
    apiGet<Persona[]>("/api/v1/evaluation/personas"),
  ]);
  if (!dataset.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const format = await getFormatter();
  const d = dataset.data;
  const latest = (d.versions[0]?.number ?? d.version) === d.version;
  return (
    <>
      <h1>
        <bdi>{pickText(d.names, locale) || t("untitled")}</bdi>
      </h1>
      <EvaluationTabs current="agents" />
      <p>
        <Link href={`/admin/evaluation/agents/${d.agentId}/suite`}>{t("dataset.back")}</Link>
      </p>
      <DatasetEditor key={d.version} initial={d} personas={personas.ok ? personas.data : []} latest={latest} />
      {d.versions.length > 0 ? (
        <section className="agents-section" aria-labelledby="eval-dataset-versions">
          <h2 id="eval-dataset-versions">{t("dataset.versions")}</h2>
          <div className="agents-table">
            <table data-testid="eval-dataset-versions">
              <thead>
                <tr>
                  <th scope="col">{t("dashboard.version")}</th>
                  <th scope="col">{t("run.cases")}</th>
                  <th scope="col">{t("dataset.locales")}</th>
                  <th scope="col">{t("dataset.classification")}</th>
                  <th scope="col">{t("dataset.source")}</th>
                  <th scope="col">{t("dataset.created")}</th>
                </tr>
              </thead>
              <tbody>
                {d.versions.map((v) => (
                  <tr key={v.number}>
                    <th scope="row">
                      <Link href={`/admin/evaluation/datasets/${d.id}?version=${v.number}`} aria-current={v.number === d.version ? "page" : undefined}>
                        {t("dashboard.versionNumber", { number: String(v.number) })}
                      </Link>
                    </th>
                    <td data-number>{v.caseCount}</td>
                    <td>{v.locales.map((l) => t(`locales.${l}`)).join(t("listSeparator")) || t("none")}</td>
                    <td>{t(`classification.${v.classification}`)}</td>
                    <td>{t.has(`dataset.sources.${v.source}`) ? t(`dataset.sources.${v.source}`) : v.source}</td>
                    <td>{format.dateTime(new Date(v.createdAt), { dateStyle: "medium", timeStyle: "short" })}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}
    </>
  );
}
