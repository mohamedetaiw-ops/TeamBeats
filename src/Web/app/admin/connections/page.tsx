import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { AllowlistEntry, FeatureView, Microsoft365Connection, Pack, PackConnection } from "@/lib/integrations/types";
import { ConnectionList } from "@/features/connections/ConnectionList";
import { Microsoft365Connections } from "@/features/connections/Microsoft365Connections";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/** The Connections area: Microsoft 365 and connector packs (spec 006 US5, spec 017 T005, T007, T012). */
export default async function ConnectionsPage({ searchParams }: { searchParams: Promise<{ consent?: string }> }) {
  const { consent } = await searchParams;
  await connectionsSection();
  const [connections, packs, allowlist, microsoft, catalogue] = await Promise.all([
    apiGet<PackConnection[]>("/api/v1/integrations/pack-connections"),
    apiGet<Pack[]>("/api/v1/integrations/packs"),
    apiGet<AllowlistEntry[]>("/api/v1/integrations/allowlist"),
    apiGet<Microsoft365Connection[]>("/api/v1/integrations/connections"),
    apiGet<FeatureView[]>("/api/v1/integrations/catalogue/microsoft365"),
  ]);
  if (!connections.ok || !packs.ok) {
    notFound();
  }
  const t = await getTranslations("connections");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      {microsoft.ok && catalogue.ok ? (
        <Microsoft365Connections
          initial={microsoft.data}
          catalogue={catalogue.data}
          consent={consent === "granted" || consent === "failed" ? consent : null}
        />
      ) : null}
      <ConnectionList initial={connections.data} packs={packs.data} allowlist={allowlist.ok ? allowlist.data : null} />
    </>
  );
}
