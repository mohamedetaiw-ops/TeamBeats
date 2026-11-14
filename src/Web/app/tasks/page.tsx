import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { Delegation, InboxItem } from "@/lib/services/types";
import { myTasksHref, spacesApi, type MyTask, type SpacesRights } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { DelegationPanel } from "@/features/services/DelegationPanel";
import { TaskInbox } from "@/features/services/TaskInbox";
import { MyTasks } from "@/features/spaces/MyTasks";
import { PageHeader } from "@/components/ui/PageHeader";
import { SectionNav } from "@/components/ui/SectionNav";
import { StateView } from "@/components/ui/StateView";

/**
 * The task inbox with delegation while away as one line above it (spec 016 US3, US4, FR-020, FR-021), with My tasks from project and team spaces as its
 * own tab (spec 029 FR-010) when spaces are in the organization's plan.
 */
export default async function TasksPage({ searchParams }: { searchParams: Promise<{ tab?: string }> }) {
  const { tab } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("services");
  const spaces = await apiGet<SpacesRights>(`${spacesApi}/rights`);
  const withSpaces = spaces.ok && spaces.data.enabled;
  const mine = withSpaces && tab === "mine";
  const tabs = withSpaces ? (
    <SectionNav
      label={t("inbox.tabsLabel")}
      items={[
        { key: "inbox", href: "/tasks", label: t("inbox.approvalsTab") },
        { key: "mine", href: myTasksHref, label: t("inbox.myTasksTab") },
      ]}
      current={mine ? "mine" : "inbox"}
    />
  ) : null;

  if (mine) {
    const tasks = await apiGet<MyTask[]>(`${spacesApi}/my-tasks`);
    return (
      <>
        <Header me={me.data} breadcrumbs={[{ label: t("inbox.pageTitle"), href: "/tasks" }]} />
        <main id="main" className="page">
          <PageHeader title={t("inbox.pageTitle")} icon="tasks" nav={tabs ?? undefined} />
          {tasks.ok ? <MyTasks initial={tasks.data} /> : <StateView kind="error" title={t("inbox.myTasksFailed")} />}
        </main>
      </>
    );
  }

  const [inbox, delegation, units] = await Promise.all([
    apiGet<InboxItem[]>("/api/v1/tasks"),
    apiGet<{ delegation: Delegation | null }>("/api/v1/me/delegation"),
    listUnits(),
  ]);
  if (!inbox.ok) {
    notFound();
  }
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("inbox.pageTitle"), href: "/tasks" }]} />
      <main id="main" className="page">
        <PageHeader title={t("inbox.pageTitle")} icon="tasks" nav={tabs ?? undefined} />
        <DelegationPanel initial={delegation.ok ? delegation.data.delegation : null} />
        <TaskInbox items={inbox.data} meId={me.data.id} units={units.ok ? units.data : []} />
      </main>
    </>
  );
}
