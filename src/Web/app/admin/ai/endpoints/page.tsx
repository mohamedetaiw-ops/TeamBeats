import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { PageHeader } from "@/components/ui/PageHeader";
import type { PrivateEndpointsView, ProviderKeyView } from "@/lib/ai/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { PrivateEndpoints } from "@/features/ai/PrivateEndpoints";
import { ProviderKey } from "@/features/ai/ProviderKey";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** Private model endpoints (spec 013 US6, T025) and, where the installation offers it, the organization's own OpenRouter key (FR-020). Who may see or change them, and whether the package allows it, is the API's answer. */
export default async function AiPrivateEndpointsPage() {
  await aiSection();
  const [endpoints, key] = await Promise.all([
    apiGet<PrivateEndpointsView>("/api/v1/ai/private-endpoints"),
    apiGet<ProviderKeyView>("/api/v1/ai/provider-keys/openrouter"),
  ]);
  if (!endpoints.ok) notFound();
  const t = await getTranslations("ai");
  return (
    <>
      <PageHeader title={t("endpoints.title")} nav={<AiTabs current="endpoints" />} />
      <PrivateEndpoints initial={endpoints.data} />
      {key.ok && key.data.offered ? <ProviderKey initial={key.data} /> : null}
    </>
  );
}
