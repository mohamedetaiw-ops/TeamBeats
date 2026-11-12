import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/ui/PageHeader";
import { apiGet } from "@/lib/api";
import { CONNECTION_TYPES, isConnectionType, type WizardStep } from "@/lib/integrations/catalogue";
import type { AllowlistEntry, FeatureView, Microsoft365Connection, Pack, PackConnection, WebhookEventChoice } from "@/lib/integrations/types";
import { ChooseType, EnableStep, Microsoft365TestStep, SetupStep, TestStep, type TypeChoice } from "@/features/connections/AddConnectionWizard";
import { WizardSteps } from "@/features/connections/parts";
import { connectionsSection } from "@/features/connections/guard";
import "@/features/connections/connections.css";

type Query = { type?: string; id?: string; step?: string };

/**
 * The "Add connection" wizard (spec 006 FR-016): choose a type, set it up, test it, turn it on. Each step has its own
 * address, so Back and a reload keep the place, and Microsoft's consent can return to it. Which types the package
 * includes, and every rule of every step, come from the API.
 */
export default async function NewConnectionPage({ searchParams }: { searchParams: Promise<Query> }) {
  const query = await searchParams;
  await connectionsSection();
  const t = await getTranslations("connections");
  const kind = isConnectionType(query.type) ? query.type : null;
  const step: WizardStep = !kind ? "choose" : !query.id ? "setup" : query.step === "enable" ? "enable" : "test";

  let content: React.ReactNode;
  if (step === "choose" || step === "setup") {
    const [packs, catalogue, webhookEvents] = await Promise.all([
      apiGet<Pack[]>("/api/v1/integrations/packs"),
      apiGet<FeatureView[]>("/api/v1/integrations/catalogue/microsoft365"),
      kind === "webhook" ? apiGet<WebhookEventChoice[]>("/api/v1/integrations/webhook-events") : Promise.resolve(null),
    ]);
    if (!packs.ok) notFound();
    const features = catalogue.ok ? catalogue.data : [];
    const types: TypeChoice[] = CONNECTION_TYPES.flatMap((k): TypeChoice[] => {
      if (k === "microsoft365") return catalogue.ok ? [{ kind: k, inPackage: features.some((f) => f.inPackage) }] : [];
      const pack = packs.data.find((p) => p.kind === k);
      return pack ? [{ kind: k, inPackage: pack.inPackage }] : [];
    });
    if (step === "choose" || !kind) {
      content = <ChooseType types={types} />;
    } else if (!types.some((c) => c.kind === kind && c.inPackage)) {
      content = (
        <p className="notice">
          {t("wizard.setup.notAvailable")} <Link href="/admin/connections/new">{t("wizard.setup.changeType")}</Link>
        </p>
      );
    } else {
      content = <SetupStep kind={kind} catalogue={features} webhookEvents={webhookEvents?.ok ? webhookEvents.data : []} />;
    }
  } else if (kind === "microsoft365") {
    const found = await apiGet<Microsoft365Connection>(`/api/v1/integrations/connections/${encodeURIComponent(query.id!)}`);
    if (!found.ok) notFound();
    const c = found.data;
    content = step === "test" ? <Microsoft365TestStep connection={c} /> : <EnableStep kind="microsoft365" id={c.id} name={c.displayName} status={c.status} enabled={c.enabled} />;
  } else {
    const [found, allowlist] = await Promise.all([
      apiGet<PackConnection>(`/api/v1/integrations/pack-connections/${encodeURIComponent(query.id!)}`),
      step === "test" ? apiGet<AllowlistEntry[]>("/api/v1/integrations/allowlist") : Promise.resolve(null),
    ]);
    if (!found.ok || found.data.kind !== kind) notFound();
    const c = found.data;
    content =
      step === "test" ? (
        <TestStep connection={c} allowlist={allowlist?.ok ? allowlist.data : null} />
      ) : (
        <EnableStep kind={c.kind} id={c.id} name={c.displayName} status={c.status} enabled={c.enabled} />
      );
  }

  return (
    <>
      <p>
        <Link href="/admin/connections">{t("back")}</Link>
      </p>
      <PageHeader title={t("wizard.title")} description={t("wizard.intro")} icon="plus" />
      <WizardSteps current={step} />
      <div className="connections">{content}</div>
    </>
  );
}
