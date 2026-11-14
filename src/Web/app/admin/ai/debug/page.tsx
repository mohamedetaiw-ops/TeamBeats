import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { DebugWindow, ModelPolicyView } from "@/lib/ai/types";
import { AiTabs } from "@/features/ai/AiTabs";
import { DebugWindows } from "@/features/ai/DebugWindows";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** Debug windows (spec 013 US8, T016). */
export default async function AiDebugPage() {
  await aiSection();
  const [windows, policy] = await Promise.all([apiGet<DebugWindow[]>("/api/v1/ai/debug-windows"), apiGet<ModelPolicyView>("/api/v1/ai/policy")]);
  if (!windows.ok) notFound();
  const t = await getTranslations("ai");
  // Opening follows the policy page's answer on the manage right; the API checks it again on every call.
  const canManage = policy.ok && policy.data.readOnlyReason !== "ai.policy.read_only_permission";
  return (
    <>
      <AdminHeader section="ai" title={t("debug.title")} nav={<AiTabs current="debug" />} />
      <DebugWindows initial={windows.data} canManage={canManage} />
    </>
  );
}
