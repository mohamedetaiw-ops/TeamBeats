import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, spaceHref, spaceMembersHref, spacesApi, spacesHref, type SpaceGuests, type SpaceMember, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Guests } from "@/features/spaces/Guests";
import { Members } from "@/features/spaces/Members";
import { SpaceNav } from "@/features/spaces/parts";

/** The members of a space (spec 029 FR-002), and for its owners its guests (US4). */
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
  // Only owners are answered; anyone else simply sees no guest section.
  const guests = await apiGet<SpaceGuests>(`${spacesApi}/${id}/guests`);
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
        {guests.ok ? <Guests spaceId={id} initial={guests.data} /> : null}
      </main>
    </>
  );
}
