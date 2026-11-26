import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceTimeHref, spacesApi, spacesHref, type SpaceView, type TimeTotals as Totals } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { SpaceNav } from "@/features/spaces/parts";
import { TimeTotals } from "@/features/spaces/TimeTotals";

/** Time logged in a space, for its owners (spec 029 R3 US9). */
export default async function SpaceTimePage({ params }: { params: Promise<{ id: string }> }) {
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
  const totals = await apiGet<Totals>(`${spacesApi}/${id}/time`);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("timeTitle"), href: spaceTimeHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("timeTitle")} eyebrow={space.data.name} icon="clock"
          nav={<SpaceNav id={id} current="time" settings={space.data.rights.owns || space.data.rights.manages} owns={space.data.rights.owns} files={!!space.data.libraryId} discussion={!!space.data.communityId} />} />
        {totals.ok ? <TimeTotals spaceId={id} initial={totals.data} /> : <StateView kind="denied" title={t("timeDeniedTitle")} message={t("timeDenied")} />}
      </main>
    </>
  );
}
