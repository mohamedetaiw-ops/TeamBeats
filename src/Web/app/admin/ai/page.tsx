import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { CatalogueEntry, ModelPolicyView } from "@/lib/ai/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { PolicyEditor } from "@/features/ai/PolicyEditor";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** Model policy (spec 013 US1, T016). People who only see usage for their units go straight to AI usage. */
export default async function AiPolicyPage() {
  await aiSection();
  const [policy, catalogue] = await Promise.all([apiGet<ModelPolicyView>("/api/v1/ai/policy"), apiGet<CatalogueEntry[]>("/api/v1/ai/catalogue")]);
  if (!policy.ok && policy.status === 403) redirect("/admin/ai/usage");
  if (!policy.ok || !catalogue.ok) notFound();
  const t = await getTranslations("ai");
  return (
    <>
      <h1>{t("policy.title")}</h1>
      <AiTabs current="policy" />
      <PolicyEditor catalogue={catalogue.data} initial={policy.data} />
    </>
  );
}
