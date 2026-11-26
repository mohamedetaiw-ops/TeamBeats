import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingHref, floorPlanEditorHref, manageBookingHref, manageSiteHref, pick, type FloorMapView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { FloorPlanEditor } from "@/features/booking/FloorPlanEditor";

/** The floor plan editor for the site's facilities administrators (spec 031 R3 US7). */
export default async function FloorPlanEditorPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const map = await apiGet<FloorMapView>(`${bookingApi}/floors/${id}/map`);
  if (!map.ok && map.status === 404) {
    notFound();
  }
  const t = await getTranslations("booking");
  const locale = await getLocale();
  const name = map.ok ? pick(map.data.names, locale) : t("floorEditor.pageTitle");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("manage.pageTitle"), href: manageBookingHref },
          ...(map.ok ? [{ label: t("floorEditor.site"), href: manageSiteHref(map.data.siteId) }] : []),
          { label: name, href: floorPlanEditorHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("floorEditor.pageTitle")} eyebrow={name} description={t("floorEditor.pageIntro")} icon="building" />
        {map.ok && map.data.manages ? (
          <FloorPlanEditor initial={map.data} />
        ) : (
          <StateView kind={map.ok || map.status === 403 ? "denied" : "error"} title={t("floorEditor.deniedTitle")} message={t("floorEditor.denied")} />
        )}
      </main>
    </>
  );
}
