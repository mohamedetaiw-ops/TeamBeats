import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { controlPoliciesHref, controlsHref, nameIn, policyHref, type LibraryFramework, type PolicyControlsView } from "@/lib/policies/types";
import { Badge } from "@/components/ui/Badge";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { PoliciesNav } from "@/features/policies/PoliciesNav";
import { ControlMappingEditor } from "@/features/policies/ControlMappingEditor";
import "@/features/policies/policies.css";

/**
 * The controls and regulation clauses a policy maps to (spec 025 FR-015), each linking to the policies of that
 * control, with the mapping editor for compliance officers. The API decides who sees and changes it.
 */
export default async function PolicyControlsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<PolicyControlsView>(`/api/v1/policies/${encodeURIComponent(id)}/controls`);
  if (!view.ok) {
    notFound();
  }
  const v = view.data;
  const library = v.canEdit ? await apiGet<LibraryFramework[]>("/api/v1/policies/control-library") : null;
  const t = await getTranslations("policies.controls");
  const locale = await getLocale();
  const name = nameIn(v.policy.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("register"), href: "/policies" },
          { label: name, href: policyHref(v.policy.id) },
          { label: t("title"), href: controlsHref(v.policy.id) },
        ]}
      />
      <main id="main" className="page policies-page">
        <PageHeader title={t("heading", { name })} description={t("description")} icon="policy" nav={<PoliciesNav current={null} />} />
        <section className="policies-card" aria-labelledby="mapped-controls">
          <h2 id="mapped-controls">{t("mapped")}</h2>
          {v.controls.length === 0 ? (
            <StateView kind="empty" title={t("none")} message="" />
          ) : (
            <ul className="policies-list" data-testid="mapped-controls">
              {v.controls.map((c) => (
                <li key={`${c.framework}:${c.code}`}>
                  {c.known ? (
                    <Link href={controlPoliciesHref(c.framework, c.code)}>
                      <bdi>{t("codeLabel", { code: c.code, title: c.titles ? nameIn(c.titles, locale) : "" })}</bdi>
                    </Link>
                  ) : (
                    <bdi>{c.code}</bdi>
                  )}{" "}
                  <span className="policies-meta">
                    <bdi>{c.frameworkNames ? t("frameworkLabel", { name: nameIn(c.frameworkNames, locale), version: c.frameworkVersion ?? "" }) : c.framework}</bdi>
                  </span>
                  {c.known ? null : (
                    <>
                      {" "}
                      <Badge tone="warning">{t("unknown")}</Badge>
                    </>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
        {v.canEdit && library?.ok ? <ControlMappingEditor policyId={v.policy.id} initial={v.controls.filter((c) => c.known)} library={library.data} /> : null}
      </main>
    </>
  );
}
