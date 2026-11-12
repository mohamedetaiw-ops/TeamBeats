import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { newSpaceHref, spacesApi, spacesHref, type SpacesRights, type TemplateOption } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CreateSpace } from "@/features/spaces/CreateSpace";

/** A new space from a template (spec 029 US1). Whether the viewer may create one is the API's answer. */
export default async function NewSpacePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("spaces");
  const [rights, templates] = await Promise.all([apiGet<SpacesRights>(`${spacesApi}/rights`), apiGet<TemplateOption[]>(`${spacesApi}/templates`)]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: t("create.pageTitle"), href: newSpaceHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("create.pageTitle")} description={t("create.pageIntro")} icon="plus" />
        {rights.ok && rights.data.canCreate && templates.ok ? (
          <CreateSpace templates={templates.data} />
        ) : (
          <StateView kind="denied" title={t("create.deniedTitle")} message={t("create.denied")} />
        )}
      </main>
    </>
  );
}
