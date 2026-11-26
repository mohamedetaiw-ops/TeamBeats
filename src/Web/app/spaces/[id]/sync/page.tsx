import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceSyncHref, spacesApi, spacesHref, type SpaceSync, type SpaceView, type TaskBoard } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { SpaceNav } from "@/features/spaces/parts";
import { SyncPanel } from "@/features/spaces/SyncPanel";

/** Planner and Jira sync of the space's task lists, for its owners (spec 029 R3 US10). */
export default async function SpaceSyncPage({ params }: { params: Promise<{ id: string }> }) {
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
  const [sync, board] = await Promise.all([apiGet<SpaceSync>(`${spacesApi}/${id}/sync`), apiGet<TaskBoard>(`${spacesApi}/${id}/tasks`)]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("syncTitle"), href: spaceSyncHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("syncTitle")} eyebrow={space.data.name} icon="link"
          nav={<SpaceNav id={id} current="sync" settings={space.data.rights.owns || space.data.rights.manages} owns={space.data.rights.owns} files={!!space.data.libraryId} discussion={!!space.data.communityId} />} />
        {sync.ok ? (
          <SyncPanel spaceId={id} initial={sync.data} lists={board.ok ? board.data.lists.map((l) => ({ id: l.id, name: l.name })) : []} />
        ) : (
          <StateView kind="denied" title={t("syncDeniedTitle")} message={t("syncDenied")} />
        )}
      </main>
    </>
  );
}
