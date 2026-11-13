import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { complianceApi, complianceHref, pick, projectHref, type ProjectView, type ReadinessView } from "@/lib/compliance";
import { Header } from "@/components/shell/Header";
import { Badge } from "@/components/ui/Badge";
import { PageHeader } from "@/components/ui/PageHeader";
import { ControlTable } from "@/features/compliance/ControlTable";
import { ProjectSettings } from "@/features/compliance/ProjectSettings";
import { Readiness } from "@/features/compliance/Readiness";
import "@/features/compliance/compliance.css";

/**
 * A compliance project (spec 026 US7): readiness, the controls, and for its managers the settings and sharing. A
 * project the viewer may not see is not found; one shared to their space opens read-only.
 */
export default async function ComplianceProjectPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const [project, readiness] = await Promise.all([apiGet<ProjectView>(`${complianceApi}/projects/${id}`), apiGet<ReadinessView>(`${complianceApi}/projects/${id}/readiness`)]);
  if (!project.ok) {
    notFound();
  }
  const [t, locale] = await Promise.all([getTranslations("compliance"), getLocale()]);
  const p = project.data.project;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: complianceHref },
          { label: p.name, href: projectHref(id) },
        ]}
      />
      <main id="main" className="page compliance">
        <PageHeader
          title={p.name}
          eyebrow={`${pick(p.frameworkNames, locale)} ${p.frameworkVersion}`}
          description={p.orgUnitNames ? t("list.scope", { unit: pick(p.orgUnitNames, locale) }) : t("list.wholeTenant")}
          icon="policy"
          actions={
            <>
              {p.status === "archived" ? <Badge tone="neutral">{t("status.archived")}</Badge> : null}
              {p.rights.readOnly ? <Badge tone="info" icon="eye">{t("list.readOnly")}</Badge> : null}
            </>
          }
        />
        {readiness.ok ? <Readiness view={readiness.data} /> : null}
        <section className="compliance-section" aria-labelledby="compliance-controls">
          <h2 id="compliance-controls">{t("controls.title")}</h2>
          <ControlTable controls={project.data.controls} />
        </section>
        {p.rights.canManage ? <ProjectSettings project={p} version={project.data.version} /> : null}
      </main>
    </>
  );
}
