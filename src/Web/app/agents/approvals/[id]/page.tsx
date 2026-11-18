import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AgentApprovalView } from "@/lib/agents/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { AgentApprovalDetail } from "@/features/agents/AgentApprovalDetail";

/**
 * An agent action waiting for approval (spec 027 US4). The API shows it only to someone who holds its task; the task
 * ID from the inbox shows the decision panel, and the API checks the assignment again when the approver decides.
 */
export default async function AgentApprovalPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const approval = await apiGet<AgentApprovalView>(`/api/v1/agents/approvals/${id}`);
  if (!approval.ok) {
    notFound();
  }
  const t = await getTranslations("agents");
  const taskId = approval.data.taskId ?? (task && /^[0-9a-f-]{36}$/i.test(task) ? task : null);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("approval.inbox"), href: "/tasks" },
          { label: t("approval.title"), href: `/agents/approvals/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("approval.title")} icon="services" />
        <AgentApprovalDetail approval={approval.data} taskId={taskId} />
      </main>
    </>
  );
}
