import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, roomsHref, type CommunityView, type RoomView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { RoomList } from "@/features/engagement/Rooms";

/** A community's discussion rooms (spec 023 R3 US9). The API decides who reads them and who opens new ones. */
export default async function CommunityRoomsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const community = await apiGet<CommunityView>(`/api/v1/engagement/communities/${id}`);
  if (!community.ok) {
    notFound();
  }
  const rooms = await apiGet<RoomView[]>(`/api/v1/engagement/communities/${id}/rooms`);
  const t = await getTranslations("engagement");
  const locale = await getLocale();
  const name = localName(community.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: name, href: communityHref(id) },
          { label: t("rooms.pageTitle"), href: roomsHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("rooms.pageTitle")} description={name} icon="chat" />
        {rooms.ok ? (
          <RoomList community={community.data} initial={rooms.data} locales={me.data.tenant.enabledLocales} />
        ) : rooms.status === 403 ? (
          <StateView kind="denied" title={t("rooms.unavailableTitle")} message={t("rooms.unavailable")} />
        ) : (
          <StateView kind="denied" title={t("community.closedTitle")} message={t("community.joinToRead")} />
        )}
      </main>
    </>
  );
}
