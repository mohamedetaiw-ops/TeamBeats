import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceTimelineHref, spacesApi, spacesHref, type SpaceView, type TimelineView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { SpaceNav } from "@/features/spaces/parts";
import { Timeline } from "@/features/spaces/Timeline";

/** The plan of a space on a timeline (spec 029 R3 US8). */
export default async function SpaceTimelinePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
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
  const timeline = space.data.rights.reads ? await apiGet<TimelineView>(`${spacesApi}/${id}/timeline`) : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("timelineTitle"), href: spaceTimelineHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("timelineTitle")} eyebrow={space.data.name} icon="calendar"
          nav={<SpaceNav id={id} current="timeline" settings={space.data.rights.owns || space.data.rights.manages} owns={space.data.rights.owns} files={!!space.data.libraryId} discussion={!!space.data.communityId} />} />
        {timeline?.ok ? (
          <Timeline spaceId={id} view={timeline.data} today={new Date().toISOString().slice(0, 10)} />
        ) : (
          <StateView kind="denied" title={t("privateTitle")} message={t("private")} />
        )}
      </main>
    </>
  );
}
