import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { evidenceApi, evidenceHref, type EvidencePackView, type PolicyListItem, type RegisterRights } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { EvidencePacks } from "@/features/policies/EvidencePacks";
import "@/features/policies/policies.css";

/**
 * Evidence packs for auditors (spec 025 US8 scenarios 1 and 2). A compliance officer asks for packs and shares them;
 * with a space, the packs shared with that space for its readers. The API refuses anyone else and audits each step.
 */
export default async function EvidencePacksPage({ searchParams }: { searchParams: Promise<{ space?: string }> }) {
  const { space } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const spaceId = space && /^[0-9a-f-]{36}$/i.test(space) ? space : null;
  const packs = await apiGet<EvidencePackView[]>(spaceId ? `${evidenceApi}?spaceId=${spaceId}` : evidenceApi);
  if (!packs.ok) {
    notFound();
  }
  const [rights, list] = spaceId ? [null, null] : await Promise.all([apiGet<RegisterRights>("/api/v1/policies/rights"), apiGet<PolicyListItem[]>("/api/v1/policies")]);
  const policies = list?.ok ? list.data.filter((p) => p.currentVersion !== null).map((p) => ({ id: p.id, code: p.code, names: p.names })) : [];
  const t = await getTranslations("policies.evidence");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: t("title"), href: evidenceHref },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader
          title={t("title")}
          description={t(spaceId ? "sharedDescription" : "description")}
          icon="policy"
          nav={<PoliciesNav current="evidence" exemptions={rights?.ok === true && rights.data.exemptions === true} evidence />}
        />
        <EvidencePacks policies={policies} packs={packs.data} spaceView={spaceId !== null} />
      </main>
    </>
  );
}
