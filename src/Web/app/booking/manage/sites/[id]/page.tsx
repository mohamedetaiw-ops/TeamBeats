import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingHref, manageBookingHref, manageSiteHref, pick, type BookingRights, type SiteDetail } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { SiteAdmin } from "@/features/booking/ManageBooking";

/** One site for its facilities administrators (spec 031 US1): floors, zones, resources, rules and QR labels. */
export default async function ManageSitePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const [detail, rights] = await Promise.all([apiGet<SiteDetail>(`${bookingApi}/manage/sites/${id}`), apiGet<BookingRights>(`${bookingApi}/rights`)]);
  if (!detail.ok) {
    notFound();
  }
  const t = await getTranslations("booking");
  const name = pick(detail.data.site.names, await getLocale());
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("manage.pageTitle"), href: manageBookingHref },
          { label: name, href: manageSiteHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={name} description={t("manage.siteIntro", { zone: detail.data.site.timeZone })} icon="building" />
        <SiteAdmin initial={detail.data} inPlan={rights.ok ? rights.data.kinds : []} />
      </main>
    </>
  );
}
