import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingDetailHref, bookingHref, myBookingsHref, type BookingView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { BookingDetail } from "@/features/booking/MyBookings";

/** One booking (spec 031 US2). The API answers its holder, the person who made it and the site's administrators. */
export default async function BookingDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const booking = await apiGet<BookingView>(`${bookingApi}/bookings/${id}`);
  if (!booking.ok) {
    notFound();
  }
  const t = await getTranslations("booking");
  const own = booking.data.holderId === me.data.id || booking.data.bookedBy === me.data.id;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("mine.pageTitle"), href: myBookingsHref },
          { label: t("mine.detailTitle"), href: bookingDetailHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("mine.detailTitle")} icon="calendar" />
        <BookingDetail initial={booking.data} own={own} />
      </main>
    </>
  );
}
