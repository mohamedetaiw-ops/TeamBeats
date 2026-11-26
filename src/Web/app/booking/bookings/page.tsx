import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { bookingApi, bookingHref, myBookingsHref, visitsHref, type BookingRights, type BookingView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { ButtonLink } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { MyBookings } from "@/features/booking/MyBookings";

/** My bookings (spec 031 US2): from yesterday on, including those the viewer made for others. */
export default async function MyBookingsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("booking");
  const [bookings, rights] = await Promise.all([apiGet<BookingView[]>(`${bookingApi}/bookings/mine`), apiGet<BookingRights>(`${bookingApi}/rights`)]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("mine.pageTitle"), href: myBookingsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader
          title={t("mine.pageTitle")}
          description={t("mine.pageIntro")}
          icon="calendar"
          actions={
            rights.ok && rights.data.visitors && rights.data.book ? (
              <ButtonLink href={visitsHref} icon="user">
                {t("visits.pageTitle")}
              </ButtonLink>
            ) : undefined
          }
        />
        {bookings.ok ? (
          <MyBookings initial={bookings.data} />
        ) : (
          <StateView kind={bookings.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
