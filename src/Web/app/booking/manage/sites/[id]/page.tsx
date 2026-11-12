import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingHref, manageBookingHref, manageSiteHref, pick, type BookingRights, type ClosureView, type SiteDetail } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { SiteAdmin } from "@/features/booking/ManageBooking";
import { SiteClosures } from "@/features/booking/SiteClosures";

/** Today's date in the site's time zone, as an ISO date. */
function siteToday(timeZone: string): string {
  try {
    return new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  } catch {
    return new Date().toISOString().slice(0, 10);
  }
}

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
  const [detail, rights, closures] = await Promise.all([
    apiGet<SiteDetail>(`${bookingApi}/manage/sites/${id}`),
    apiGet<BookingRights>(`${bookingApi}/rights`),
    apiGet<ClosureView[]>(`${bookingApi}/manage/sites/${id}/closures`),
  ]);
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
        {detail.data.site.status === "active" ? (
          <SiteClosures siteId={id} initial={closures.ok ? closures.data : []} today={siteToday(detail.data.site.timeZone)} />
        ) : null}
      </main>
    </>
  );
}
