import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { OutcomesView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { OutcomeBaselines } from "@/features/evaluation/OutcomeBaselines";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** The windows a reader can choose; the API accepts any number of days up to a year. */
const WINDOWS = [7, 30, 90] as const;

/** Business outcomes of the tenant's agents against its baselines (spec 034 US9 scenario 1). */
export default async function EvaluationOutcomesPage({ searchParams }: { searchParams: Promise<{ days?: string }> }) {
  await evaluationSection();
  const { days } = await searchParams;
  const chosen = WINDOWS.find((w) => String(w) === days);
  const outcomes = await apiGet<OutcomesView>(`/api/v1/evaluation/outcomes${chosen ? `?days=${chosen}` : ""}`);
  if (!outcomes.ok) notFound();
  const t = await getTranslations("evaluation");
  const locale = await getLocale();
  return (
    <>
      <AdminHeader section="evaluation" title={t("outcomes.title")} nav={<EvaluationTabs current="outcomes" />} />
      <div className="agents">
        <p>{t("outcomes.intro")}</p>
        {!outcomes.data.available ? (
          <p role="status">{t("outcomes.unavailable")}</p>
        ) : (
          <nav aria-label={t("outcomes.window")} className="agents-row">
            {WINDOWS.map((w) => (
              <Link key={w} href={`/admin/evaluation/outcomes?days=${w}`} aria-current={outcomes.data.days === w ? "page" : undefined}>
                {t("outcomes.days", { days: String(w) })}
              </Link>
            ))}
          </nav>
        )}
      </div>
      {outcomes.data.available ? <OutcomeBaselines key={outcomes.data.days} initial={outcomes.data} locale={locale} /> : null}
    </>
  );
}
