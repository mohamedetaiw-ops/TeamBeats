import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { manageSpacesHref, spacesApi, spacesHref, type SpacesRights } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ManageSpaces, type SpaceRules, type TemplateView } from "@/features/spaces/ManageSpaces";

/** Space rules and templates for spaces administrators (spec 029 FR-012, FR-017). The API refuses anyone else. */
export default async function ManageSpacesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("spaces");
  const [rules, templates, rights] = await Promise.all([
    apiGet<SpaceRules>(`${spacesApi}/manage/settings`),
    apiGet<TemplateView[]>(`${spacesApi}/manage/templates`),
    apiGet<SpacesRights>(`${spacesApi}/rights`),
  ]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: t("manage.pageTitle"), href: manageSpacesHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("manage.pageTitle")} description={t("manage.pageIntro")} icon="settings" />
        {rules.ok && templates.ok ? (
          <ManageSpaces initialRules={rules.data} initialTemplates={templates.data} guestsInPlan={rights.ok && rights.data.guests} />
        ) : (
          <StateView kind="denied" title={t("manage.deniedTitle")} message={t("manage.denied")} />
        )}
      </main>
    </>
  );
}
