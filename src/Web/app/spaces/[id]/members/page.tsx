import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceMembersHref, spacesApi, spacesHref, type SpaceMember, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Members } from "@/features/spaces/Members";
import { SpaceNav } from "@/features/spaces/parts";

/** The members of a space (spec 029 FR-002). */
export default async function SpaceMembersPage({ params }: { params: Promise<{ id: string }> }) {
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
  const members = await apiGet<SpaceMember[]>(`${spacesApi}/${id}/members`);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("membersTitle"), href: spaceMembersHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("membersTitle")} eyebrow={space.data.name} icon="people"
          nav={<SpaceNav id={id} current="members" settings={space.data.rights.owns || space.data.rights.manages} files={!!space.data.libraryId} discussion={!!space.data.communityId} />} />
        {members.ok ? <Members space={space.data} initial={members.data} /> : <StateView kind="denied" title={t("privateTitle")} message={t("private")} />}
      </main>
    </>
  );
}
