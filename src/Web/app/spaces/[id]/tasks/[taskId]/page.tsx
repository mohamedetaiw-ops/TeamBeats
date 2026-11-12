import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceTasksHref, spacesApi, spacesHref, taskHref, type MilestoneView, type SpaceMember, type SpaceView, type TaskDetail } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { TaskView } from "@/features/spaces/TaskView";

/** One task of a space (spec 029 US2). */
export default async function TaskPage({ params }: { params: Promise<{ id: string; taskId: string }> }) {
  const { id, taskId } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id) || !isGuid(taskId)) {
    notFound();
  }
  const [space, task, members, milestones] = await Promise.all([
    apiGet<SpaceView>(`${spacesApi}/${id}`),
    apiGet<TaskDetail>(`${spacesApi}/${id}/tasks/${taskId}`),
    apiGet<SpaceMember[]>(`${spacesApi}/${id}/members`),
    apiGet<MilestoneView[]>(`${spacesApi}/${id}/milestones`),
  ]);
  if (!space.ok || !task.ok) {
    notFound();
  }
  const t = await getTranslations("spaces");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("tasksTitle"), href: spaceTasksHref(id) },
          { label: task.data.task.title, href: taskHref(id, taskId) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={task.data.task.title} eyebrow={space.data.name} icon="tasks" />
        <TaskView spaceId={id} initial={task.data} members={members.ok ? members.data : []} milestones={milestones.ok ? milestones.data : []} />
      </main>
    </>
  );
}
