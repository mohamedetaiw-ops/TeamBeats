import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceCalendarHref, spaceHref, spacesApi, spacesHref, type SpaceCalendarData, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { SpaceNav } from "@/features/spaces/parts";
import { TeamCalendar } from "@/features/spaces/TeamCalendar";

const monthPattern = /^\d{4}-(0[1-9]|1[0-2])$/;

/** A space's team calendar for one month (spec 029 US6). */
export default async function SpaceCalendarPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ month?: string }> }) {
  const { id } = await params;
  const { month } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const space = await apiGet<SpaceView>(`${spacesApi}/${id}`);
  if (!space.ok) {
    notFound();
  }
  const t = await getTranslations("spaces");
  let range = "";
  if (month && monthPattern.test(month)) {
    const [y, m] = month.split("-").map(Number) as [number, number];
    const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
    range = `?from=${month}-01&to=${month}-${String(last).padStart(2, "0")}`;
  }
  const calendar = space.data.rights.reads ? await apiGet<SpaceCalendarData>(`${spacesApi}/${id}/calendar${range}`) : null;
  const today = new Date().toISOString().slice(0, 10);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("calendarTitle"), href: spaceCalendarHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("calendarTitle")} eyebrow={space.data.name} icon="calendar"
          nav={<SpaceNav id={id} current="calendar" settings={space.data.rights.owns || space.data.rights.manages} files={!!space.data.libraryId} discussion={!!space.data.communityId} />} />
        {calendar?.ok ? (
          <TeamCalendar key={calendar.data.from} spaceId={id} initial={calendar.data} today={today} />
        ) : (
          <StateView kind="denied" title={t("privateTitle")} message={t("private")} />
        )}
      </main>
    </>
  );
}
