import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick } from "@/lib/services/types";
import type { DecisionModelView } from "@/lib/processes";
import { DecisionEditor } from "@/features/processes/DecisionEditor";

/** One decision table (spec 024 FR-007). A table the person may not design or read is not found. */
export default async function DecisionPage({ params }: { params: Promise<{ key: string }> }) {
  const { key } = await params;
  const [decision, units] = await Promise.all([apiGet<DecisionModelView>(`/api/v1/workflow/decisions/${encodeURIComponent(key)}`), listUnits()]);
  if (!decision.ok) {
    notFound();
  }
  const t = await getTranslations("processes.decisions");
  const locale = await getLocale();
  return (
    <>
      <p>
        <Link href="/admin/decisions">{t("back")}</Link>
      </p>
      <h1>
        <bdi>{pick(decision.data.names, locale, decision.data.key)}</bdi>
      </h1>
      <DecisionEditor decisionKey={decision.data.key} initial={decision.data} units={units.ok ? units.data : []} />
    </>
  );
}
