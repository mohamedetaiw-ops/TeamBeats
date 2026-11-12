import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingHref, kioskHref, pick, type KioskView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { Kiosk } from "@/features/booking/MyBookings";

/** A site's kiosk (spec 031 FR-008): the signed-in person checks in to their bookings there today. */
export default async function KioskPage({ params }: { params: Promise<{ site: string }> }) {
  const { site } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(site)) {
    notFound();
  }
  const kiosk = await apiGet<KioskView>(`${bookingApi}/sites/${site}/kiosk`);
  if (!kiosk.ok) {
    notFound();
  }
  const t = await getTranslations("booking");
  const locale = await getLocale();
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("checkIn.kioskTitle"), href: kioskHref(site) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("checkIn.kioskTitle")} description={pick(kiosk.data.siteNames, locale)} icon="building" />
        <Kiosk initial={kiosk.data} />
      </main>
    </>
  );
}
