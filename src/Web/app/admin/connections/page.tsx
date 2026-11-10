import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AllowlistEntry, Pack, PackConnection } from "@/lib/integrations/types";
import { ConnectionList } from "@/features/connections/ConnectionList";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/** The Connections area for connector packs (spec 017 T007, T012). */
export default async function ConnectionsPage() {
  await connectionsSection();
  const [connections, packs, allowlist] = await Promise.all([
    apiGet<PackConnection[]>("/api/v1/integrations/pack-connections"),
    apiGet<Pack[]>("/api/v1/integrations/packs"),
    apiGet<AllowlistEntry[]>("/api/v1/integrations/allowlist"),
  ]);
  if (!connections.ok || !packs.ok) {
    notFound();
  }
  const t = await getTranslations("connections");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <ConnectionList initial={connections.data} packs={packs.data} allowlist={allowlist.ok ? allowlist.data : null} />
    </>
  );
}
