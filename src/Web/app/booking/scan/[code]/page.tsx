import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { bookingApi, bookingHref, scanHref, type ScanView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { ScanCheckIn } from "@/features/booking/MyBookings";

/** Where a printed resource code leads (spec 031 US4): the viewer's booking of the resource for now, and check-in. */
export default async function ScanPage({ params }: { params: Promise<{ code: string }> }) {
  const { code } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f]{32}$/i.test(code)) {
    notFound();
  }
  const scan = await apiGet<ScanView>(`${bookingApi}/scan/${code}`);
  if (!scan.ok) {
    notFound();
  }
  const t = await getTranslations("booking");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: bookingHref },
          { label: t("checkIn.pageTitle"), href: scanHref(code) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("checkIn.pageTitle")} icon="check" />
        <ScanCheckIn initial={scan.data} code={code.toLowerCase()} />
      </main>
    </>
  );
}
