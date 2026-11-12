import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { ButtonLink } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { apiGet } from "@/lib/api";
import { NEW_CONNECTION, overviewRows, wizardHref } from "@/lib/integrations/catalogue";
import type { AllowlistEntry, Microsoft365Connection, PackConnection } from "@/lib/integrations/types";
import { Allowlist } from "@/features/connections/Allowlist";
import { ConnectionsOverview } from "@/features/connections/ConnectionsOverview";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

/**
 * The Connections area (spec 006 US5, FR-010, FR-016): the tenant's established connections for managing them, and
 * the allowed hosts. Adding a connection is the separate wizard at /admin/connections/new.
 */
export default async function ConnectionsPage({ searchParams }: { searchParams: Promise<{ consent?: string; connection?: string }> }) {
  const { consent, connection } = await searchParams;
  await connectionsSection();
  const [packs, allowlist, microsoft] = await Promise.all([
    apiGet<PackConnection[]>("/api/v1/integrations/pack-connections"),
    apiGet<AllowlistEntry[]>("/api/v1/integrations/allowlist"),
    apiGet<Microsoft365Connection[]>("/api/v1/integrations/connections"),
  ]);
  if (!packs.ok) {
    notFound();
  }
  const t = await getTranslations("connections");
  const m365 = microsoft.ok ? microsoft.data : [];
  const consented = connection ? m365.find((c) => c.id === connection) : undefined;
  return (
    <>
      <PageHeader
        title={t("title")}
        description={t("intro")}
        icon="link"
        actions={
          <ButtonLink variant="primary" icon="plus" href={NEW_CONNECTION}>
            {t("overview.add")}
          </ButtonLink>
        }
      />
      {consent === "granted" ? (
        <div role="status" className="notice">
          <p>
            {t("m365.consentGranted")}{" "}
            {consented ? <Link href={wizardHref("microsoft365", consented.id, "test")}>{t("overview.continueSetup", { name: consented.displayName })}</Link> : null}
          </p>
        </div>
      ) : null}
      {consent === "failed" ? (
        <div role="alert" className="notice notice-error">
          <p>{t("m365.consentFailed")}</p>
        </div>
      ) : null}
      <div className="connections">
        <ConnectionsOverview rows={overviewRows(m365, packs.data)} />
        <Allowlist initial={allowlist.ok ? allowlist.data : null} returnTo="/admin/connections" />
      </div>
    </>
  );
}
