import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spacesApi, spacesHref, type SpaceOverview, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Overview } from "@/features/spaces/Overview";
import { SpaceNav } from "@/features/spaces/parts";

/** A space's overview (spec 029 US3). A private space the viewer is not in shows what the directory shows, and nothing more. */
export default async function SpacePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const space = await apiGet<SpaceView>(`${spacesApi}/${id}`);
  if (!space.ok) {
    notFound();
  }
  const t = await getTranslations("spaces");
  const overview = space.data.rights.reads ? await apiGet<SpaceOverview>(`${spacesApi}/${id}/overview`) : null;
  const settings = space.data.rights.owns || space.data.rights.manages;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={space.data.name} eyebrow={t(`kind.${space.data.kind}`)} icon="folder" nav={<SpaceNav id={id} current="overview" settings={settings} files={!!space.data.libraryId} discussion={!!space.data.communityId} />} />
        {overview?.ok ? (
          <Overview space={space.data} initial={overview.data} />
        ) : (
          <StateView kind="denied" title={t("privateTitle")} message={t("private")} />
        )}
      </main>
    </>
  );
}
