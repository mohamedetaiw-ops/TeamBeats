import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingHref, pick, receptionHref, type ReceptionDay, type ReceptionSite } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Reception } from "@/features/booking/Reception";

/** Reception (spec 031 R3 US8): the day's visitors of a site the viewer works reception for, today by default. */
export default async function ReceptionPage({ searchParams }: { searchParams: Promise<{ site?: string }> }) {
  const query = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("booking");
  const locale = await getLocale();
  const sites = await apiGet<ReceptionSite[]>(`${bookingApi}/reception/sites`);
  const site = sites.ok ? (sites.data.find((s) => isGuid(query.site ?? "") && s.id === query.site) ?? sites.data[0]) : undefined;
  const day = site ? await apiGet<ReceptionDay>(`${bookingApi}/reception/sites/${site.id}/visits`) : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("reception.pageTitle"), href: receptionHref(site?.id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("reception.pageTitle")} eyebrow={site ? pick(site.names, locale) : undefined} description={t("reception.pageIntro")} icon="people" />
        {!sites.ok ? (
          <StateView kind={sites.status === 403 ? "denied" : "error"} title={t("reception.deniedTitle")} message={t("reception.denied")} />
        ) : !site || !day ? (
          <StateView kind="empty" title={t("reception.noSitesTitle")} message={t("reception.noSites")} icon="building" />
        ) : day.ok ? (
          <Reception key={site.id} sites={sites.data} initial={day.data} />
        ) : (
          <StateView kind="error" title={t("reception.failed")} message={t("reception.unavailable")} />
        )}
      </main>
    </>
  );
}
