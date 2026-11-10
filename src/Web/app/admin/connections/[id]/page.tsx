import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { PackConnection, RestActionView, RunPage } from "@/lib/integrations/types";
import { ActionBuilder } from "@/features/connections/ActionBuilder";
import { ConnectionDetail } from "@/features/connections/ConnectionDetail";
import { RunHistory } from "@/features/connections/RunHistory";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/** One connection: state and lifecycle, custom REST actions, and run history (spec 017 T007, T010, T012). */
export default async function ConnectionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  await connectionsSection();
  const path = `/api/v1/integrations/pack-connections/${encodeURIComponent(id)}`;
  const connection = await apiGet<PackConnection>(path);
  if (!connection.ok) {
    notFound();
  }
  const [actions, runs] = await Promise.all([
    connection.data.kind === "custom_rest" ? apiGet<RestActionView[]>(`${path}/actions`) : Promise.resolve(null),
    apiGet<RunPage>(`/api/v1/integrations/connections/${encodeURIComponent(id)}/runs`),
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
      <ConnectionDetail initial={connection.data} />
      {actions?.ok ? <ActionBuilder connectionId={connection.data.id} initial={actions.data} /> : null}
      <RunHistory connectionId={connection.data.id} initial={runs.ok ? runs.data : null} />
    </>
  );
}
