import { headers } from "next/headers";
import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingHref, manageBookingHref, manageSiteHref, siteLabelsHref, type QrLabel } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { Labels } from "@/features/booking/ManageBooking";

/** The labels to print for a site's resources (spec 031 US1 scenario 1). */
export default async function SiteLabelsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const labels = await apiGet<QrLabel[]>(`${bookingApi}/manage/sites/${id}/labels`);
  if (!labels.ok) {
    notFound();
  }
  const t = await getTranslations("booking");
  const host = (await headers()).get("host") ?? "";
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("manage.pageTitle"), href: manageBookingHref },
          { label: t("manage.site"), href: manageSiteHref(id) },
          { label: t("manage.labels"), href: siteLabelsHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("manage.labels")} description={t("manage.labelsIntro")} icon="download" />
        <Labels labels={labels.data} origin={host ? `https://${host}` : ""} />
      </main>
    </>
  );
}
