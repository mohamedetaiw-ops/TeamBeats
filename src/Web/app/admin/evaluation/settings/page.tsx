import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { MetricDefinition, SettingsView } from "@/lib/evaluation/types";
import { EvaluationTabs } from "@/features/evaluation/EvaluationTabs";
import { SettingsEditor } from "@/features/evaluation/SettingsEditor";
import { evaluationSection } from "@/features/evaluation/guard";
import "@/features/agents/agents.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/** Evaluation settings for the whole tenant (spec 034 US10). Readers see them; only those who manage evaluation policy change them. */
export default async function EvaluationSettingsPage() {
  await evaluationSection();
  const [settings, catalog] = await Promise.all([apiGet<SettingsView>("/api/v1/evaluation/settings"), apiGet<MetricDefinition[]>("/api/v1/evaluation/metrics")]);
  if (!settings.ok) notFound();
  const t = await getTranslations("evaluation");
  return (
    <>
      <AdminHeader section="evaluation" title={t("settings.title")} nav={<EvaluationTabs current="settings" />} />
      <SettingsEditor initial={settings.data} catalog={catalog.ok ? catalog.data : []} />
    </>
  );
}
