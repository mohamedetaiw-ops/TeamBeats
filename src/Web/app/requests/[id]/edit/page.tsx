import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick, type Prefill, type RequestDetail } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { RequestForm } from "@/features/services/RequestForm";

/**
 * Change a returned request and submit it again (spec 016 lifecycle). The form is the version the request was filled
 * with; the API refuses anyone but the filer and any request that was not returned.
 */
export default async function EditRequestPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const detail = await apiGet<RequestDetail>(`/api/v1/requests/${encodeURIComponent(id)}`);
  if (!detail.ok || detail.data.request.status !== "returned" || detail.data.request.requester.id !== me.data.id) {
    notFound();
  }
  const r = detail.data.request;
  const forWhom = r.onBehalfOf ? `?for=${r.onBehalfOf.id}` : "";
  const [prefill, units] = await Promise.all([apiGet<Prefill>(`/api/v1/services/${r.serviceId}/prefill${forWhom}`), listUnits()]);
  const t = await getTranslations("services");
  const locale = await getLocale();
  const last = [...detail.data.tracking].reverse().find((e) => e.event === "returned");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("requests.title"), href: "/requests" },
          { label: r.number, href: `/requests/${id}` },
        ]}
      />
      <main id="main" className="page">
        <h1>
          <bdi>{t("form.changeTitle", { name: pick(r.serviceNames, locale) })}</bdi>
        </h1>
        {last?.comment ? (
          <div className="notice" role="note">
            <p>{t("form.returnedWith")}</p>
            <p>{last.comment}</p>
          </div>
        ) : null}
        <RequestForm
          target={{ kind: "resubmit", requestId: id }}
          form={detail.data.form}
          initial={detail.data.data}
          readOnly={prefill.ok ? prefill.data.readOnly : []}
          units={units.ok ? units.data : []}
          existingAttachments={detail.data.attachments}
        />
      </main>
    </>
  );
}
