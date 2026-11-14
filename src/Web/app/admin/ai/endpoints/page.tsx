import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { PageHeader } from "@/components/ui/PageHeader";
import type { PrivateEndpointsView } from "@/lib/ai/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { PrivateEndpoints } from "@/features/ai/PrivateEndpoints";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** Private model endpoints (spec 013 US6, T025). Who may see or change them, and whether the package allows it, is the API's answer. */
export default async function AiPrivateEndpointsPage() {
  await aiSection();
  const endpoints = await apiGet<PrivateEndpointsView>("/api/v1/ai/private-endpoints");
  if (!endpoints.ok) notFound();
  const t = await getTranslations("ai");
  return (
    <>
      <PageHeader title={t("endpoints.title")} nav={<AiTabs current="endpoints" />} />
      <PrivateEndpoints initial={endpoints.data} />
    </>
  );
}
