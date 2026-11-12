import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceSettingsHref, spacesApi, spacesHref, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Settings } from "@/features/spaces/Settings";
import { SpaceNav } from "@/features/spaces/parts";

/** A space's settings, archive and restore (spec 029 FR-001, FR-013), for owners and spaces administrators. */
export default async function SpaceSettingsPage({ params }: { params: Promise<{ id: string }> }) {
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
  const allowed = space.data.rights.owns || space.data.rights.manages;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("settingsTitle"), href: spaceSettingsHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("settingsTitle")} eyebrow={space.data.name} icon="settings" nav={<SpaceNav id={id} current="settings" settings={allowed} files={!!space.data.libraryId} discussion={!!space.data.communityId} />} />
        {allowed ? <Settings initial={space.data} /> : <StateView kind="denied" title={t("settingsDeniedTitle")} message={t("settingsDenied")} />}
      </main>
    </>
  );
}
