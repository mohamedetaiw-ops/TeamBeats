"use client";

import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/Tabs";
import type { ProcessModelSummary } from "@/lib/processes";
import { MonitoringPanel } from "./MonitoringPanel";
import { OperationsPanel } from "./OperationsPanel";

/** Instances and monitoring as two tabs of one page; each tab asks the API, which decides what the person may see. */
export function ProcessOperationsTabs({ processes }: { processes: ProcessModelSummary[] }) {
  const t = useTranslations("processes.operations");
  return (
    <Tabs
      label={t("pageTitle")}
      tabs={[
        { key: "instances", label: t("instancesTab"), content: <OperationsPanel processes={processes} /> },
        { key: "monitoring", label: t("monitoringTab"), content: <MonitoringPanel processes={processes} /> },
      ]}
    />
  );
}
