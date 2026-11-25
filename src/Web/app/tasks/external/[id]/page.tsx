import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ExternalTask } from "@/lib/processes";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ExternalTaskForm } from "@/features/processes/ExternalTaskForm";

/**
 * A Camunda 8 user task assigned to the signed-in person (spec 024 US8): its details and the form that completes it.
 * The API shows it only to its assignee and only in a package with advanced process features.
 */
export default async function ExternalTaskPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("processes.externalTask");
  const inbox = await getTranslations("services.inbox");
  const task = await apiGet<ExternalTask>(`/api/v1/workflow/external-tasks/${encodeURIComponent(id)}`);
  if (!task.ok && task.status === 404) {
    notFound();
  }
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: inbox("pageTitle"), href: "/tasks" }]} />
      <main id="main" className="page">
        {task.ok ? (
          <>
            <PageHeader title={task.data.name} description={task.data.processName ?? undefined} icon="tasks" eyebrow={t("eyebrow")} />
            <ExternalTaskForm initial={task.data} />
          </>
        ) : (
          <>
            <PageHeader title={t("title")} icon="tasks" />
            <StateView kind={task.status === 403 ? "denied" : "error"} title={task.status === 403 ? t("denied") : t("failed")} />
          </>
        )}
      </main>
    </>
  );
}
