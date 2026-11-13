import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { complianceApi, complianceHref, libraryHref, type ComplianceRights, type LibraryView, type ProjectSummary } from "@/lib/compliance";
import { Header } from "@/components/shell/Header";
import { ButtonLink } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ProjectList } from "@/features/compliance/ProjectList";
import { NewProject } from "@/features/compliance/NewProject";

/**
 * Compliance projects (spec 026 US7): the projects the viewer may see, and for compliance officers a form to start one
 * from the control library. Plans without compliance projects get the API's refusal as a notice.
 */
export default async function CompliancePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("compliance");
  const [rights, projects] = await Promise.all([apiGet<ComplianceRights>(`${complianceApi}/rights`), apiGet<ProjectSummary[]>(`${complianceApi}/projects`)]);
  const library = rights.ok && rights.data.canManage ? await apiGet<LibraryView>(`${complianceApi}/library`) : null;
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("pageTitle"), href: complianceHref }]} />
      <main id="main" className="page compliance">
        <PageHeader
          title={t("pageTitle")}
          description={t("pageIntro")}
          icon="policy"
          actions={
            rights.ok && rights.data.canView ? (
              <ButtonLink href={libraryHref} icon="book">
                {t("library.title")}
              </ButtonLink>
            ) : null
          }
        />
        {!rights.ok || !rights.data.enabled ? (
          <StateView kind="unsupported" title={t("unavailableTitle")} message={t("unavailable")} />
        ) : projects.ok ? (
          <>
            <ProjectList projects={projects.data} />
            {library?.ok ? <NewProject frameworks={library.data.frameworks} /> : null}
          </>
        ) : (
          <StateView kind="error" title={t("failedTitle")} message={t("failed")} />
        )}
      </main>
    </>
  );
}
