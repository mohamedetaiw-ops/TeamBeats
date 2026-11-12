import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceTasksHref, spacesApi, spacesHref, type SpaceView, type TaskBoard } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Board } from "@/features/spaces/Board";
import { SpaceNav } from "@/features/spaces/parts";

/** A space's tasks as a list or a board (spec 029 US2). */
export default async function SpaceTasksPage({ params }: { params: Promise<{ id: string }> }) {
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
  const board = space.data.rights.reads ? await apiGet<TaskBoard>(`${spacesApi}/${id}/tasks`) : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("tasksTitle"), href: spaceTasksHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("tasksTitle")} eyebrow={space.data.name} icon="tasks"
          nav={<SpaceNav id={id} current="tasks" settings={space.data.rights.owns || space.data.rights.manages} />} />
        {board?.ok ? <Board spaceId={id} initial={board.data} /> : <StateView kind="denied" title={t("privateTitle")} message={t("private")} />}
      </main>
    </>
  );
}
