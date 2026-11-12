import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AgentJob, CaptureView, ClockFilePage, FinanceHandOff, ConnectorTool, InboundCounts, PackConnection, RestActionView, RunPage, SyncAgent, WebhookEventChoice } from "@/lib/integrations/types";
import { ActionBuilder } from "@/features/connections/ActionBuilder";
import { ClockFilesPanel } from "@/features/connections/ClockFilesPanel";
import { ConnectionDetail } from "@/features/connections/ConnectionDetail";
import { FinanceHandOffsPanel } from "@/features/connections/FinanceHandOffsPanel";
import { ConnectorToolsPanel } from "@/features/connections/ConnectorToolsPanel";
import { RunHistory } from "@/features/connections/RunHistory";
import { SyncAgentPanel } from "@/features/connections/SyncAgentPanel";
import { WebhookInboundPanel } from "@/features/connections/WebhookInboundPanel";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/** One connection: state and lifecycle, custom REST actions, agent tools, sync agents, clock data files, finance hand-offs, run history and payload capture (spec 017 T007, T009, T010, T012, T015, T016, FR-007). */
export default async function ConnectionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  await connectionsSection();
  const path = `/api/v1/integrations/pack-connections/${encodeURIComponent(id)}`;
  const connection = await apiGet<PackConnection>(path);
  if (!connection.ok) {
    notFound();
  }
  const webhook = connection.data.kind === "webhook";
  const onprem = connection.data.kind === "onprem_ad";
  const clock = connection.data.kind === "clock_import";
  const finance = connection.data.kind === "finance_rest";
  const lms = connection.data.kind === "lms_rest";
  const google = connection.data.kind === "google_workspace";
  const hr = connection.data.kind === "hr_rest" || connection.data.kind === "hr_sftp";
  const toolable = !webhook && !onprem && !clock && !finance && !lms && !google && !hr;
  const capturable = !onprem && !clock && !finance && !lms && !google && !hr;
  const [actions, runs, webhookEvents, inbound, agents, agentJobs, tools, capture, clockFiles, handOffs] = await Promise.all([
    connection.data.kind === "custom_rest" ? apiGet<RestActionView[]>(`${path}/actions`) : Promise.resolve(null),
    apiGet<RunPage>(`/api/v1/integrations/connections/${encodeURIComponent(id)}/runs`),
    webhook ? apiGet<WebhookEventChoice[]>("/api/v1/integrations/webhook-events") : Promise.resolve(null),
    webhook ? apiGet<InboundCounts>(`${path}/inbound`) : Promise.resolve(null),
    onprem ? apiGet<SyncAgent[]>(`${path}/agents`) : Promise.resolve(null),
    onprem ? apiGet<AgentJob[]>(`${path}/agent-jobs`) : Promise.resolve(null),
    toolable ? apiGet<ConnectorTool[]>(`${path}/tools`) : Promise.resolve(null),
    capturable ? apiGet<CaptureView>(`/api/v1/integrations/connections/${encodeURIComponent(id)}/capture`) : Promise.resolve(null),
    clock ? apiGet<ClockFilePage>(`${path}/clock-files`) : Promise.resolve(null),
    finance ? apiGet<FinanceHandOff[]>(`${path}/finance-handoffs`) : Promise.resolve(null),
  ]);
  const t = await getTranslations("connections");
  return (
    <>
      <p>
        <Link href="/admin/connections">{t("back")}</Link>
      </p>
      <h1>
        <bdi>{connection.data.displayName}</bdi>
      </h1>
      <ConnectionDetail initial={connection.data} webhookEvents={webhookEvents?.ok ? webhookEvents.data : []} />
      {webhook && connection.data.settings && "inbound" in connection.data.settings && inbound?.ok ? <WebhookInboundPanel counts={inbound.data} /> : null}
      {onprem ? <SyncAgentPanel connectionId={connection.data.id} agents={agents?.ok ? agents.data : []} jobs={agentJobs?.ok ? agentJobs.data : []} /> : null}
      {clock ? <ClockFilesPanel connectionId={connection.data.id} initial={clockFiles?.ok ? clockFiles.data : null} /> : null}
      {finance ? <FinanceHandOffsPanel items={handOffs?.ok ? handOffs.data : null} /> : null}
      {actions?.ok ? <ActionBuilder connectionId={connection.data.id} initial={actions.data} /> : null}
      {tools?.ok ? <ConnectorToolsPanel connectionId={connection.data.id} actions={connection.data.actions} tools={tools.data} /> : null}
      <RunHistory connectionId={connection.data.id} initial={runs.ok ? runs.data : null} capture={capture?.ok ? capture.data : null} />
    </>
  );
}
