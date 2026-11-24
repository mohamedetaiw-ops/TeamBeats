import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { pickText } from "@/lib/agents/types";
import type { OrgUnitView } from "@/lib/admin/types";
import type { MetricDefinition, UnitSettingsView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { UnitEvalSettingsEditor } from "@/features/evaluation/UnitEvalSettingsEditor";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

const ID = /^[0-9a-f-]{36}$/i;

/**
 * Evaluation settings by organization unit (spec 034 US10, FR-020): choose a unit, see what the levels above it set and
 * the rules in force, and set the unit's own stricter thresholds and datasets. The API decides which units each person
 * may read or change.
 */
export default async function EvaluationUnitsPage({ searchParams }: { searchParams: Promise<{ unit?: string }> }) {
  await evaluationSection();
  const { unit } = await searchParams;
  const units = await apiGet<OrgUnitView[]>("/api/v1/org/units");
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  const list = units.ok ? [...units.data].filter((u) => u.status === "active").sort((a, b) => a.level - b.level) : [];
  const chosen = unit && ID.test(unit) ? list.find((u) => u.id === unit) : undefined;
  const [settings, catalog] = chosen
    ? await Promise.all([
        apiGet<UnitSettingsView>(`/api/v1/evaluation/units/${chosen.id}/settings`),
        apiGet<MetricDefinition[]>("/api/v1/evaluation/metrics"),
      ])
    : [null, null];
  if (unit && !chosen) notFound();
  return (
    <>
      <AdminHeader section="evaluation" title={t("units.title")} nav={<EvaluationTabs current="units" />} />
      <div className="agents">
        <p>{t("units.intro")}</p>
        {list.length === 0 ? <p>{t("units.noUnits")}</p> : null}
        {list.length > 0 ? (
          <nav aria-label={t("units.choose")}>
            <ul className="agents-row">
              {list.map((u) => (
                <li key={u.id}>
                  <Link href={`/admin/evaluation/units?unit=${u.id}`} aria-current={chosen?.id === u.id ? "page" : undefined}>
                    <bdi>{pickText(u.names, locale) || u.code}</bdi>
                  </Link>
                </li>
              ))}
            </ul>
          </nav>
        ) : null}
        {chosen ? <p data-testid="eval-unit-chosen">{t("units.editing", { unit: pickText(chosen.names, locale) || chosen.code })}</p> : null}
        {chosen && settings && !settings.ok ? <p role="status">{t("units.hidden")}</p> : null}
        {chosen && settings?.ok && !settings.data.available ? <p role="status">{t("units.unavailable")}</p> : null}
      </div>
      {chosen && settings?.ok && settings.data.available ? (
        <UnitEvalSettingsEditor key={chosen.id} initial={settings.data} catalog={catalog?.ok ? catalog.data : []} locale={locale} />
      ) : null}
    </>
  );
}
