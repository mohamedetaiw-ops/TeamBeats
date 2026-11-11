import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { LeaveRequest } from "@/lib/leave";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { LeaveRequestDetail } from "@/features/leave/LeaveRequestDetail";

/**
 * One leave request (spec 022 US1). The API decides who may read it; a task ID from the inbox shows the decision panel,
 * and the API checks the assignment again when the approver decides.
 */
export default async function LeaveRequestPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const request = await apiGet<LeaveRequest>(`/api/v1/hr/leave/requests/${id}`);
  if (!request.ok) {
    notFound();
  }
  const t = await getTranslations("leave");
  const taskId = task && /^[0-9a-f-]{36}$/i.test(task) ? task : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/leave" },
          { label: request.data.number, href: `/leave/requests/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("detail.title", { number: request.data.number })} icon="calendar" />
        <LeaveRequestDetail initial={request.data} taskId={taskId} />
      </main>
    </>
  );
}
