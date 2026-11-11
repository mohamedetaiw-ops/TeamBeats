import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { AttendanceRequest } from "@/lib/attendance";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { AttendanceRequestDetail } from "@/features/attendance/AttendanceRequestDetail";

/**
 * One attendance correction or compensation request (spec 022 US3). The API decides who may read it; a task ID from
 * the inbox shows the decision panel, and the API checks the assignment again when the manager decides.
 */
export default async function AttendanceRequestPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const request = await apiGet<AttendanceRequest>(`/api/v1/hr/attendance/requests/${id}`);
  if (!request.ok) {
    notFound();
  }
  const t = await getTranslations("attendance");
  const taskId = task && /^[0-9a-f-]{36}$/i.test(task) ? task : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/attendance" },
          { label: t("detail.title"), href: `/attendance/requests/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("detail.title")} icon="clock" />
        <AttendanceRequestDetail initial={request.data} taskId={taskId} />
      </main>
    </>
  );
}
