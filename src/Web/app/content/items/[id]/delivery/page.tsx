import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { editHref, variantFor, type DeliveryReport, type ItemView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { DeliveryReportTable } from "@/features/content/DeliveryReportTable";
import "@/features/content/content.css";
import { Widget } from "@/components/ui/Widget";
import { PageHeader } from "@/components/ui/PageHeader";

/** Who received a critical announcement, as counts (spec 015 US8): people reached and each channel by status. */
export default async function DeliveryReportPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [item, report] = await Promise.all([
    apiGet<ItemView>(`/api/v1/content/items/${encodeURIComponent(id)}`),
    apiGet<DeliveryReport>(`/api/v1/content/items/${encodeURIComponent(id)}/delivery-report`),
  ]);
  if (!item.ok || !report.ok) {
    notFound();
  }
  const t = await getTranslations("content");
  const locale = await getLocale();
  const title = variantFor(item.data, locale)?.title ?? "";
  const number = new Intl.NumberFormat(locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("items.listTitle"), href: "/content/items" },
          { label: title, href: editHref(id) },
          { label: t("delivery.title"), href: `${editHref(id)}/delivery` },
        ]}
      />
      <main id="main" className="page content-manage">
        <PageHeader title={t("delivery.heading", { title })} description={t("delivery.intro")} icon="megaphone" />
        <dl className="content-stats">
          <div>
            <dt>{t("delivery.recipients")}</dt>
            <dd>{number.format(report.data.recipients)}</dd>
          </div>
          <div>
            <dt>{t("delivery.notifications")}</dt>
            <dd>{number.format(report.data.notifications)}</dd>
          </div>
        </dl>
        <Widget title={t("delivery.byChannel")}>
          <DeliveryReportTable channels={report.data.channels} />
        </Widget>
      </main>
    </>
  );
}
