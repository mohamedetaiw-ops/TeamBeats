import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { editHref, variantFor, type AcknowledgementSummary, type ItemView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { AckReport } from "@/features/content/AckReport";
import "@/features/content/content.css";

/** Who has confirmed a critical announcement (spec 009 US2): counts, channels, the deadline and the completion report. */
export default async function AcknowledgementsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [item, summary] = await Promise.all([
    apiGet<ItemView>(`/api/v1/content/items/${encodeURIComponent(id)}`),
    apiGet<AcknowledgementSummary>(`/api/v1/content/items/${encodeURIComponent(id)}/acknowledgements`),
  ]);
  if (!item.ok || !summary.ok) {
    notFound();
  }
  const t = await getTranslations("content");
  const locale = await getLocale();
  const title = variantFor(item.data, locale)?.title ?? "";
  const s = summary.data;
  const number = new Intl.NumberFormat(locale);
  const percent = s.audience > 0 ? new Intl.NumberFormat(locale, { style: "percent" }).format(s.confirmed / s.audience) : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("items.listTitle"), href: "/content/items" },
          { label: title, href: editHref(id) },
          { label: t("acks.title"), href: `${editHref(id)}/acknowledgements` },
        ]}
      />
      <main id="main" className="page content-manage">
        <h1>{t("acks.heading", { title })}</h1>
        <dl className="content-stats">
          <div>
            <dt>{t("acks.audience")}</dt>
            <dd>{number.format(s.audience)}</dd>
          </div>
          <div>
            <dt>{t("acks.confirmed")}</dt>
            <dd>
              {number.format(s.confirmed)}
              {percent ? ` (${percent})` : ""}
            </dd>
          </div>
          <div>
            <dt>{t("acks.pending")}</dt>
            <dd>{number.format(s.pending)}</dd>
          </div>
          {Object.entries(s.byChannel).map(([channel, count]) => (
            <div key={channel}>
              <dt>{t(`acks.channel.${channel === "mobile" ? "mobile" : "web"}`)}</dt>
              <dd>{number.format(count)}</dd>
            </div>
          ))}
        </dl>
        <p className="content-meta">
          {t("acks.countedFrom", { version: s.countedFrom })}
          {s.deadlineAt ? ` ${t("acks.deadline", { date: new Intl.DateTimeFormat(locale, { dateStyle: "long", timeStyle: "short" }).format(new Date(s.deadlineAt)) })}` : ""}
        </p>
        <section className="content-card" aria-labelledby="ack-report">
          <h2 id="ack-report">{t("acks.report")}</h2>
          <AckReport itemId={id} />
        </section>
      </main>
    </>
  );
}
