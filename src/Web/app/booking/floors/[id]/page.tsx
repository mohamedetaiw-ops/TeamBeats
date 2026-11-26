import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid } from "@/lib/spaces";
import { bookingApi, bookingHref, floorMapHref, pick, type FloorMapView } from "@/lib/booking";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { FloorMap } from "@/features/booking/FloorMap";

const time = /^\d{2}:\d{2}$/;
const day = /^\d{4}-\d{2}-\d{2}$/;

/** A floor on its plan for a time (spec 031 R3 US7). Without a time, the API offers the next hour at the site. */
export default async function FloorPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ date?: string; start?: string; end?: string }> }) {
  const { id } = await params;
  const query = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const ask = new URLSearchParams();
  if (query.date && day.test(query.date) && query.start && time.test(query.start) && query.end && time.test(query.end)) {
    ask.set("date", query.date);
    ask.set("start", query.start);
    ask.set("end", query.end);
  }
  const map = await apiGet<FloorMapView>(`${bookingApi}/floors/${id}/map${ask.size ? `?${ask}` : ""}`);
  const t = await getTranslations("booking");
  if (!map.ok && map.status === 404) {
    notFound();
  }
  const locale = await getLocale();
  const name = map.ok ? pick(map.data.names, locale) : t("floor.pageTitle");
  const local = (iso: string, zone: string, part: "date" | "time") =>
    new Intl.DateTimeFormat("en-CA", part === "date" ? { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" } : { timeZone: zone, hour: "2-digit", minute: "2-digit", hourCycle: "h23" })
      .format(new Date(iso));
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("pageTitle"), href: bookingHref }, { label: name, href: floorMapHref(id) }]} />
      <main id="main" className="page">
        <PageHeader title={name} eyebrow={t("floor.pageTitle")} description={t("floor.pageIntro")} icon="building" />
        {map.ok ? (
          <FloorMap initial={map.data} date={local(map.data.startsAt, map.data.timeZone, "date")} start={local(map.data.startsAt, map.data.timeZone, "time")}
            end={local(map.data.endsAt, map.data.timeZone, "time")} />
        ) : (
          <StateView kind={map.status === 403 ? "unsupported" : "error"} title={t("floor.unavailableTitle")} message={t("floor.unavailable")} />
        )}
      </main>
    </>
  );
}
