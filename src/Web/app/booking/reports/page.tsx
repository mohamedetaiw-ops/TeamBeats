import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { bookingApi, bookingHref, bookingReportsHref, type ReportSite } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { UsageReports } from "@/features/booking/UsageReports";

/** Space usage reports (spec 031 US6) for analysts and facilities administrators. The API lists only the sites in the viewer's report scope. */
export default async function BookingReportsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("booking");
  const sites = await apiGet<ReportSite[]>(`${bookingApi}/reports/sites`);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("reports.pageTitle"), href: bookingReportsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("reports.pageTitle")} description={t("reports.pageIntro")} icon="trend-up" />
        {sites.ok ? (
          <UsageReports sites={sites.data} />
        ) : (
          <StateView kind={sites.status === 403 ? "denied" : "error"} title={t("reports.unavailableTitle")} message={t("reports.unavailable")} />
        )}
      </main>
    </>
  );
}
