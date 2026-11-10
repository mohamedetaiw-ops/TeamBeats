import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AgentJob, InboundCounts, PackConnection, RestActionView, RunPage, SyncAgent, WebhookEventChoice } from "@/lib/integrations/types";
import { ActionBuilder } from "@/features/connections/ActionBuilder";
import { ConnectionDetail } from "@/features/connections/ConnectionDetail";
import { RunHistory } from "@/features/connections/RunHistory";
import { SyncAgentPanel } from "@/features/connections/SyncAgentPanel";
import { WebhookInboundPanel } from "@/features/connections/WebhookInboundPanel";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/** One connection: state and lifecycle, custom REST actions, sync agents, and run history (spec 017 T007, T010, T012, T015). */
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
  const [actions, runs, webhookEvents, inbound, agents, agentJobs] = await Promise.all([
    connection.data.kind === "custom_rest" ? apiGet<RestActionView[]>(`${path}/actions`) : Promise.resolve(null),
    apiGet<RunPage>(`/api/v1/integrations/connections/${encodeURIComponent(id)}/runs`),
    webhook ? apiGet<WebhookEventChoice[]>("/api/v1/integrations/webhook-events") : Promise.resolve(null),
    webhook ? apiGet<InboundCounts>(`${path}/inbound`) : Promise.resolve(null),
    onprem ? apiGet<SyncAgent[]>(`${path}/agents`) : Promise.resolve(null),
    onprem ? apiGet<AgentJob[]>(`${path}/agent-jobs`) : Promise.resolve(null),
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
      {actions?.ok ? <ActionBuilder connectionId={connection.data.id} initial={actions.data} /> : null}
      <RunHistory connectionId={connection.data.id} initial={runs.ok ? runs.data : null} />
    </>
  );
}
