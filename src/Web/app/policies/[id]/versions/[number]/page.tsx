import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { nameIn, policyHref, shownLocale, versionHref, type PolicyReading } from "@/lib/policies/types";
import { Header } from "@/components/shell/Header";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { PolicyDocument } from "@/features/policies/PolicyDocument";

/** One numbered version (spec 025 US1 scenario 5); a superseded one carries the banner to the version in force. */
export default async function PolicyVersionPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string; number: string }>;
  searchParams: Promise<{ lang?: string }>;
}) {
  const { id, number } = await params;
  const { lang } = await searchParams;
  if (!/^[1-9][0-9]{0,5}$/.test(number)) {
    notFound();
  }
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const reading = await apiGet<PolicyReading>(`/api/v1/policies/${encodeURIComponent(id)}/versions/${number}`);
  if (!reading.ok) {
    notFound();
  }
  const t = await getTranslations("policies.reading");
  const locale = await getLocale();
  const wanted = lang && me.data.tenant.enabledLocales.includes(lang) ? lang : locale;
  const r = reading.data;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: nameIn(r.policy.names, locale), href: policyHref(r.policy.id) },
          { label: t("version", { number: r.version.number ?? 0 }), href: versionHref(r.policy.id, r.version.number ?? 0) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PoliciesNav current={null} />
        <PolicyDocument reading={r} locale={shownLocale(r.version, wanted, r.bindingLocale)} />
      </main>
    </>
  );
}
