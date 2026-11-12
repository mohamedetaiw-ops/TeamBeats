import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { bookingApi, bookingHref, manageBookingHref, type SiteView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ManageSites } from "@/features/booking/ManageBooking";

/** Sites for facilities administrators (spec 031 US1). The API lists only the sites in the viewer's scope. */
export default async function ManageBookingPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("booking");
  const sites = await apiGet<SiteView[]>(`${bookingApi}/manage/sites`);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("manage.pageTitle"), href: manageBookingHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("manage.pageTitle")} description={t("manage.pageIntro")} icon="settings" />
        {sites.ok ? (
          <ManageSites initial={sites.data} />
        ) : (
          <StateView kind={sites.status === 403 ? "denied" : "error"} title={t("manage.deniedTitle")} message={t("manage.denied")} />
        )}
      </main>
    </>
  );
}
