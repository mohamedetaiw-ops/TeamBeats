import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { ButtonLink } from "@/components/ui/Button";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { apiGet } from "@/lib/api";
import type { Pack } from "@/lib/integrations/types";
import { Marketplace } from "@/features/connections/Marketplace";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/** The connector marketplace (spec 017 R3): every published pack with its package state and the tenant's connections of it. */
export default async function MarketplacePage() {
  await connectionsSection();
  const packs = await apiGet<Pack[]>("/api/v1/integrations/packs");
  if (!packs.ok) notFound();
  const t = await getTranslations("connections");
  return (
    <>
      <AdminHeader
        section="connections"
        title={t("marketplace.title")}
        description={t("marketplace.intro")}
        actions={
          <ButtonLink href="/admin/connections" icon="chevron-back">
            {t("marketplace.back")}
          </ButtonLink>
        }
      />
      <div className="connections">
        <Marketplace packs={packs.data} />
      </div>
    </>
  );
}
