import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { DataChange } from "@/lib/data-changes";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { DataChangeDetail } from "@/features/data-changes/DataChangeDetail";

/**
 * One data change (spec 022 US6). The API decides who may read it; a task ID from the inbox shows the decision panel,
 * and the API checks the assignment again when HR decides.
 */
export default async function DataChangePage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const change = await apiGet<DataChange>(`/api/v1/hr/data-changes/${id}`);
  if (!change.ok) {
    notFound();
  }
  const t = await getTranslations("data_changes");
  const taskId = task && /^[0-9a-f-]{36}$/i.test(task) ? task : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/my-details" },
          { label: t("detail.title"), href: `/my-details/requests/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("detail.title")} icon="user" />
        <DataChangeDetail initial={change.data} taskId={taskId} />
      </main>
    </>
  );
}
