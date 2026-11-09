import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, policyHref, shownLocale, type MyAttestation, type PolicyReading, type PolicyView, type VersionHistoryItem } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { PolicyDocument } from "@/features/policies/PolicyDocument";
import { AttestPanel } from "@/features/policies/AttestPanel";
import { VersionList } from "@/features/policies/VersionList";

/**
 * A policy page (spec 025 US1 and US2): the version in force with numbered clauses and anchors, the attest panel when
 * the person owes an attestation, and the version history for the owner and compliance officers. A policy the person
 * may not read, or another tenant's, is not found.
 */
export default async function PolicyPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ lang?: string }> }) {
  const { id } = await params;
  const { lang } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const policy = await apiGet<PolicyView>(`/api/v1/policies/${encodeURIComponent(id)}`);
  if (!policy.ok) {
    notFound();
  }
  const p = policy.data;
  const [reading, attestation, history] = await Promise.all([
    p.currentVersion ? apiGet<PolicyReading>(`/api/v1/policies/${encodeURIComponent(id)}/current`) : null,
    apiGet<MyAttestation>(`/api/v1/policies/${encodeURIComponent(id)}/attestation`),
    p.rights.history ? apiGet<VersionHistoryItem[]>(`/api/v1/policies/${encodeURIComponent(id)}/versions`) : null,
  ]);
  const t = await getTranslations("policies.reading");
  const locale = await getLocale();
  const wanted = lang && me.data.tenant.enabledLocales.includes(lang) ? lang : locale;
  const shown = reading?.ok ? shownLocale(reading.data.version, wanted, reading.data.bindingLocale) : wanted;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: nameIn(p.names, locale), href: policyHref(p.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PoliciesNav current={null} />
        {reading?.ok ? (
          <PolicyDocument reading={reading.data} locale={shown} />
        ) : (
          <>
            <h1>
              <bdi>{nameIn(p.names, locale)}</bdi>
            </h1>
            <StateView kind="empty" title={t("noVersion")} message="" />
          </>
        )}
        {attestation.ok ? <AttestPanel policyId={p.id} initial={attestation.data} locale={shown} /> : null}
        {history?.ok && history.data.length > 0 ? <VersionList policyId={p.id} versions={history.data} /> : null}
      </main>
    </>
  );
}
