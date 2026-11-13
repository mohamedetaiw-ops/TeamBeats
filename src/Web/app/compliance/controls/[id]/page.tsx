import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { complianceApi, complianceHref, controlHref, pick, projectHref, statusTone, type ControlDetail } from "@/lib/compliance";
import { Header } from "@/components/shell/Header";
import { Badge } from "@/components/ui/Badge";
import { PageHeader } from "@/components/ui/PageHeader";
import { ControlEditor } from "@/features/compliance/ControlEditor";
import { EvidenceTasks } from "@/features/compliance/EvidenceTasks";
import "@/features/compliance/compliance.css";

/**
 * One control of a compliance project (FR-022): its owner, status, due date, linked policies and documents, and its
 * evidence tasks. Owners and managers change it; everyone else who sees the project reads it.
 */
export default async function ComplianceControlPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const detail = await apiGet<ControlDetail>(`${complianceApi}/controls/${id}`);
  if (!detail.ok) {
    notFound();
  }
  const [t, locale, format] = await Promise.all([getTranslations("compliance"), getLocale(), getFormatter()]);
  const d = detail.data;
  const c = d.control;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: complianceHref },
          { label: d.projectName, href: projectHref(d.projectId) },
          { label: c.code, href: controlHref(id) },
        ]}
      />
      <main id="main" className="page compliance">
        <PageHeader
          title={pick(c.titles, locale)}
          eyebrow={c.code}
          icon="policy"
          actions={<Badge tone={statusTone[c.status]}>{t(`status.${c.status}`)}</Badge>}
        />
        <section className="compliance-section" aria-labelledby="control-facts">
          <h2 id="control-facts">{t("control.facts")}</h2>
          <p>{t("control.ownerIs", { name: c.owner?.displayName ?? t("noOwner") })}</p>
          <p>
            {c.dueOn
              ? t("control.dueIs", { date: format.dateTime(new Date(`${c.dueOn}T12:00:00Z`), { dateStyle: "long", timeZone: "UTC" }) })
              : t("control.noDue")}
          </p>
          {d.note ? (
            <p>
              <bdi>{d.note}</bdi>
            </p>
          ) : null}
          <h3>{t("control.policies")}</h3>
          {d.policies.length === 0 ? (
            <p className="compliance-meta">{t("control.noPolicies")}</p>
          ) : (
            <ul className="compliance-list">
              {d.policies.map((p) => (
                <li key={p.id}>
                  {p.available ? (
                    <Link href={`/policies/${p.id}`}>
                      <bdi>
                        {p.code} {pick(p.names, locale)}
                      </bdi>
                    </Link>
                  ) : (
                    <span className="compliance-meta">{t("control.unavailable")}</span>
                  )}
                </li>
              ))}
            </ul>
          )}
          <h3>{t("control.documents")}</h3>
          {d.documents.length === 0 ? (
            <p className="compliance-meta">{t("control.noDocuments")}</p>
          ) : (
            <ul className="compliance-list">
              {d.documents.map((doc) => (
                <li key={doc.id}>
                  {doc.available ? (
                    <Link href={`/documents/${doc.id}`}>
                      <bdi>{pick(doc.names, locale)}</bdi>
                    </Link>
                  ) : (
                    <span className="compliance-meta">{t("control.unavailable")}</span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
        <ControlEditor detail={d} />
        <EvidenceTasks detail={d} />
      </main>
    </>
  );
}
