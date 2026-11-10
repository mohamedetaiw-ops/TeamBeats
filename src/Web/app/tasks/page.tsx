import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { Delegation, InboxItem } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { DelegationPanel } from "@/features/services/DelegationPanel";
import { ServicesNav } from "@/features/services/ServicesNav";
import { TaskInbox } from "@/features/services/TaskInbox";
import { PageHeader } from "@/components/ui/PageHeader";

/** The task inbox and delegation while away (spec 016 US3 and US4). */
export default async function TasksPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [inbox, delegation] = await Promise.all([apiGet<InboxItem[]>("/api/v1/tasks"), apiGet<{ delegation: Delegation | null }>("/api/v1/me/delegation")]);
  if (!inbox.ok) {
    notFound();
  }
  const t = await getTranslations("services");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("inbox.pageTitle"), href: "/tasks" }]} />
      <main id="main" className="page">
        <PageHeader title={t("inbox.pageTitle")} icon="tasks" nav={<ServicesNav current="tasks" />} />
        <div className="ui-split">
          <TaskInbox items={inbox.data} />
          <aside aria-label={t("delegation.title")}>
            <DelegationPanel initial={delegation.ok ? delegation.data.delegation : null} />
          </aside>
        </div>
      </main>
    </>
  );
}
