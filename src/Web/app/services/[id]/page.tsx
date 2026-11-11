import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick, type Prefill, type PreparedRequest, type ServiceDetail } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { RequestForm } from "@/features/services/RequestForm";
import { ServicesNav } from "@/features/services/ServicesNav";

/**
 * A new request (spec 016 US2): the service's published form, prefilled for the signed-in person. A service the
 * audience leaves out says it is not available to them (US1 scenario 4), without showing the form.
 */
export default async function NewRequestPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ prepared?: string }>;
}) {
  const { id } = await params;
  const { prepared: preparedId } = await searchParams;
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
  const [prefill, units, prepared] = await Promise.all([
    apiGet<Prefill>(`/api/v1/services/${encodeURIComponent(id)}/prefill`),
    listUnits(),
    preparedId ? apiGet<PreparedRequest>(`/api/v1/requests/prepared/${encodeURIComponent(preparedId)}`) : Promise.resolve(null),
  ]);
  // A request an agent prepared (spec 020 US8) fills the form, except values that come read-only from the directory.
  const draft = prepared?.ok && prepared.data.serviceId === id && prepared.data.status === "prepared" ? prepared.data : null;
  const readOnly = prefill.ok ? prefill.data.readOnly : [];
  const initial = {
    ...(prefill.ok ? prefill.data.values : {}),
    ...(draft ? Object.fromEntries(Object.entries(draft.data).filter(([k]) => !readOnly.includes(k))) : {}),
  };
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
        <PageHeader title={name} description={pick(detail.data.service.descriptions, locale) || undefined} icon="services" nav={<ServicesNav current={null} />} />
        <div className="ui-form-column">
        {draft ? <p className="notice">{t("form.prepared")}</p> : null}
        {detail.data.hasApproval ? <p className="notice">{t("form.needsApproval")}</p> : null}
        <RequestForm
          target={{ kind: "new", serviceId: id, preparedId: draft?.id }}
          form={detail.data.form}
          initial={initial}
          readOnly={readOnly}
          unconfirmed={prefill.ok ? (prefill.data.unconfirmed ?? []) : []}
          units={units.ok ? units.data : []}
          allowOnBehalf={detail.data.allowOnBehalf}
        />
        </div>
      </main>
    </>
  );
}
