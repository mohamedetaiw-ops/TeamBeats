import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { CatalogueEntry, ModelPolicyView } from "@/lib/ai/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { RoutingEditor } from "@/features/ai/RoutingEditor";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** Routing rules (spec 013 US1, FR-019, T016). */
export default async function AiRoutingPage() {
  await aiSection();
  const [policy, catalogue] = await Promise.all([apiGet<ModelPolicyView>("/api/v1/ai/policy"), apiGet<CatalogueEntry[]>("/api/v1/ai/catalogue")]);
  if (!policy.ok && policy.status === 403) redirect("/admin/ai/usage");
  if (!policy.ok || !catalogue.ok) notFound();
  const t = await getTranslations("ai");
  // The override button follows the same answer as editing; the API checks the right again when it is used.
  const canOverride = policy.data.readOnlyReason !== "ai.policy.read_only_permission";
  return (
    <>
      <h1>{t("routing.title")}</h1>
      <AiTabs current="routing" />
      <RoutingEditor catalogue={catalogue.data} initial={policy.data} canOverride={canOverride} />
    </>
  );
}
