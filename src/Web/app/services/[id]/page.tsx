import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick, type Prefill, type ServiceDetail } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { RequestForm } from "@/features/services/RequestForm";
import { ServicesNav } from "@/features/services/ServicesNav";

/**
 * A new request (spec 016 US2): the service's published form, prefilled for the signed-in person. A service the
 * audience leaves out says it is not available to them (US1 scenario 4), without showing the form.
 */
export default async function NewRequestPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("services");
  const detail = await apiGet<ServiceDetail>(`/api/v1/services/${encodeURIComponent(id)}`);
  if (!detail.ok && detail.status === 403) {
    return (
      <>
        <Header me={me.data} breadcrumbs={[{ label: t("catalogue.title"), href: "/services" }]} />
        <main id="main" className="page">
          <h1>{t("catalogue.title")}</h1>
          <StateView kind="denied" title={t("catalogue.notAvailable")} message={t("catalogue.notAvailableHint")} />
        </main>
      </>
    );
  }
  if (!detail.ok || detail.data.service.kind !== "form" || detail.data.version === null) {
    notFound();
  }
  const [prefill, units] = await Promise.all([apiGet<Prefill>(`/api/v1/services/${encodeURIComponent(id)}/prefill`), listUnits()]);
  const locale = await getLocale();
  const name = pick(detail.data.service.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("catalogue.title"), href: "/services" },
          { label: name, href: `/services/${id}` },
        ]}
      />
      <main id="main" className="page">
        <h1>
          <bdi>{name}</bdi>
        </h1>
        {pick(detail.data.service.descriptions, locale) ? <p>{pick(detail.data.service.descriptions, locale)}</p> : null}
        <ServicesNav current={null} />
        {detail.data.hasApproval ? <p className="notice">{t("form.needsApproval")}</p> : null}
        <RequestForm
          target={{ kind: "new", serviceId: id }}
          form={detail.data.form}
          initial={prefill.ok ? prefill.data.values : {}}
          readOnly={prefill.ok ? prefill.data.readOnly : []}
          units={units.ok ? units.data : []}
          allowOnBehalf={detail.data.allowOnBehalf}
        />
      </main>
    </>
  );
}
