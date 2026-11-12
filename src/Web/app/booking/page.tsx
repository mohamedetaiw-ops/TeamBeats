import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { bookingApi, bookingHref, manageBookingHref, myBookingsHref, tomorrowIso, type BookingRights, type SiteView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { ButtonLink } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { FindSpace } from "@/features/booking/FindSpace";

/** Find a space (spec 031 US2). Tomorrow is the first day offered; the API checks every rule when booking. */
export default async function BookingPage({ searchParams }: { searchParams: Promise<{ site?: string; kind?: string; zone?: string }> }) {
  const query = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("booking");
  const [rights, sites] = await Promise.all([apiGet<BookingRights>(`${bookingApi}/rights`), apiGet<SiteView[]>(`${bookingApi}/sites`)]);
  const tomorrow = tomorrowIso();
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("pageTitle"), href: bookingHref }]} />
      <main id="main" className="page">
        <PageHeader
          title={t("pageTitle")}
          description={t("pageIntro")}
          icon="building"
          actions={
            <>
              <ButtonLink href={myBookingsHref} icon="calendar">
                {t("mine.pageTitle")}
              </ButtonLink>
              {rights.ok && rights.data.manage ? (
                <ButtonLink href={manageBookingHref} icon="settings">
                  {t("manage.pageTitle")}
                </ButtonLink>
              ) : null}
            </>
          }
        />
        {rights.ok && rights.data.book && sites.ok ? (
          <FindSpace sites={sites.data} kinds={rights.data.kinds} initial={{ site: query.site, kind: query.kind, zone: query.zone, date: tomorrow }} />
        ) : (
          <StateView kind={sites.ok || sites.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
