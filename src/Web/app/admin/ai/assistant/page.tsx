import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AssistantSettings } from "@/lib/assistant/types";
import type { ModelPolicyView } from "@/lib/ai/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { AssistantRetention } from "@/features/ai/AssistantRetention";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** Assistant settings (spec 014 section 3, FR-008): how long conversations are kept. */
export default async function AiAssistantPage() {
  await aiSection();
  const [settings, policy] = await Promise.all([apiGet<AssistantSettings>("/api/v1/assistant/settings"), apiGet<ModelPolicyView>("/api/v1/ai/policy")]);
  if (!settings.ok) notFound();
  const t = await getTranslations("assistant.admin");
  // Saving follows the policy page's answer on the manage right; the API checks it again on every call.
  const canManage = policy.ok && policy.data.readOnlyReason !== "ai.policy.read_only_permission";
  return (
    <>
      <h1>{t("title")}</h1>
      <AiTabs current="assistant" />
      <AssistantRetention initial={settings.data} canManage={canManage} />
    </>
  );
}
