import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick, type RequestDetail } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { RequestDetailView } from "@/features/services/RequestDetailView";
import { ServicesNav } from "@/features/services/ServicesNav";

/**
 * Details and Tracking of a request (spec 016 FR-005). The API decides who may read it; a task ID from the inbox shows
 * the decision panel, and the API checks the assignment again when the approver decides.
 */
export default async function RequestPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [detail, units] = await Promise.all([apiGet<RequestDetail>(`/api/v1/requests/${encodeURIComponent(id)}`), listUnits()]);
  if (!detail.ok) {
    notFound();
  }
  const t = await getTranslations("services");
  const locale = await getLocale();
  const name = pick(detail.data.request.serviceNames, locale);
  const taskId = task && /^[0-9a-f-]{36}$/i.test(task) ? task : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("requests.title"), href: "/requests" },
          { label: detail.data.request.number, href: `/requests/${id}` },
        ]}
      />
      <main id="main" className="page">
        <h1>
          <bdi>{name}</bdi>
        </h1>
        <ServicesNav current={null} />
        <RequestDetailView detail={detail.data} meId={me.data.id} units={units.ok ? units.data : []} taskId={taskId} />
      </main>
    </>
  );
}
