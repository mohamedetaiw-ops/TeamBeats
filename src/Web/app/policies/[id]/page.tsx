import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { campaignsHref, complianceHref, editHref, nameIn, policyHref, reviewHref, shownLocale, type MyAttestation, type PolicyReading, type PolicyView, type VersionHistoryItem } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { PolicyDocument } from "@/features/policies/PolicyDocument";
import { AttestPanel } from "@/features/policies/AttestPanel";
import { VersionList } from "@/features/policies/VersionList";
import { AskAgentLink } from "@/features/agents/AskAgentLink";
import { ViewBeacon } from "@/features/analytics/ViewBeacon";

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
  const tm = await getTranslations("policies.manage");
  const format = await getFormatter();
  const actions = [
    p.rights.edit || p.rights.manage
      ? { href: editHref(p.id), label: tm(p.working ? (p.working.state === "in_review" ? "inReview" : "continueDraft") : p.currentVersion ? "revise" : "write") }
      : null,
    p.working?.state === "in_review" && (p.rights.review || p.rights.edit || p.rights.manage) ? { href: reviewHref(p.id), label: tm("review") } : null,
    p.rights.history ? { href: campaignsHref(p.id), label: tm("campaigns") } : null,
    p.rights.compliance && p.currentVersion ? { href: complianceHref(p.id), label: tm("compliance") } : null,
    p.rights.analytics ? { href: `${policyHref(p.id)}/analytics`, label: tm("analytics") } : null,
  ].filter((a): a is { href: string; label: string } => a !== null);
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
        {actions.length > 0 ? (
          <nav aria-label={tm("label")} className="policies-nav">
            <ul>
              {actions.map((a) => (
                <li key={a.href}>
                  <Link href={a.href}>{a.label}</Link>
                </li>
              ))}
            </ul>
          </nav>
        ) : null}
        {reading?.ok && p.rights.ask ? (
          <p>
            <Link href={`/assistant?policy=${encodeURIComponent(p.id)}`} data-testid="policy-ask">
              {t("ask")}
            </Link>
          </p>
        ) : null}
        {reading?.ok ? <AskAgentLink source="policies" itemId={p.id} /> : null}
        {p.scheduled ? <p className="policies-banner">{tm("scheduled", { number: p.scheduled.number, date: format.dateTime(new Date(p.scheduled.effectiveFrom), { dateStyle: "long" }) })}</p> : null}
        {reading?.ok ? <ViewBeacon source="policies" itemId={p.id} /> : null}
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
