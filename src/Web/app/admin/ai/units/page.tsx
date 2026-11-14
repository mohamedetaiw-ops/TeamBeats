import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { PageHeader } from "@/components/ui/PageHeader";
import type { OrgPolicySummary } from "@/lib/ai/types";
import type { OrgUnitView } from "@/lib/admin/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { UnitPolicies } from "@/features/ai/UnitPolicies";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** Unit model policies (spec 013 US7, T022). The API answers only for units in the viewer's scope. */
export default async function AiUnitPoliciesPage() {
  await aiSection();
  const [policies, units] = await Promise.all([apiGet<OrgPolicySummary[]>("/api/v1/ai/org-policies"), apiGet<OrgUnitView[]>("/api/v1/org/units")]);
  if (!policies.ok) notFound();
  const t = await getTranslations("ai");
  return (
    <>
      <PageHeader title={t("units.title")} nav={<AiTabs current="units" />} />
      <UnitPolicies initial={policies.data} units={units.ok ? units.data : []} />
    </>
  );
}
