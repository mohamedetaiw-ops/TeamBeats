import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { manageSpacesHref, newSpaceHref, spacesApi, spacesHref, type SpaceSummary, type SpacesRights } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { ButtonLink } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Directory } from "@/features/spaces/Directory";

/** The space directory (spec 029 US1). The API lists only what the viewer may know about and says what they may create. */
export default async function SpacesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("spaces");
  const [spaces, rights] = await Promise.all([apiGet<SpaceSummary[]>(spacesApi), apiGet<SpacesRights>(`${spacesApi}/rights`)]);
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("pageTitle"), href: spacesHref }]} />
      <main id="main" className="page">
        <PageHeader
          title={t("pageTitle")}
          description={t("pageIntro")}
          icon="folder"
          actions={
            <>
              {rights.ok && rights.data.canCreate ? (
                <ButtonLink href={newSpaceHref} icon="plus" variant="primary">
                  {t("create.pageTitle")}
                </ButtonLink>
              ) : null}
              {rights.ok && rights.data.canManage ? (
                <ButtonLink href={manageSpacesHref} icon="settings">
                  {t("manage.pageTitle")}
                </ButtonLink>
              ) : null}
            </>
          }
        />
        {spaces.ok ? (
          <Directory initial={spaces.data} manages={rights.ok && rights.data.canManage} />
        ) : (
          <StateView kind={spaces.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
