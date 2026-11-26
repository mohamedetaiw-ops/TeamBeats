import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { bookingApi, bookingHref, todayIn, visitsHref, type BookingView, type SiteView, type VisitView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Visits } from "@/features/booking/Visits";

/** Visitors for hosts (spec 031 R3 US8): expect a visitor, see the code, change or cancel. */
export default async function VisitsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("booking");
  const [visits, sites, bookings] = await Promise.all([
    apiGet<VisitView[]>(`${bookingApi}/visits/mine`),
    apiGet<SiteView[]>(`${bookingApi}/sites`),
    apiGet<BookingView[]>(`${bookingApi}/bookings/mine`),
  ]);
  const zone = sites.ok && sites.data[0] ? sites.data[0].timeZone : "UTC";
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("visits.pageTitle"), href: visitsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("visits.pageTitle")} description={t("visits.pageIntro")} icon="user" />
        {visits.ok && sites.ok ? (
          <Visits initial={visits.data} sites={sites.data} bookings={bookings.ok ? bookings.data : []} today={todayIn(zone)} />
        ) : (
          <StateView kind={!visits.ok && visits.status === 403 ? "unsupported" : "error"} title={t("visits.unavailableTitle")} message={t("visits.unavailable")} />
        )}
      </main>
    </>
  );
}
