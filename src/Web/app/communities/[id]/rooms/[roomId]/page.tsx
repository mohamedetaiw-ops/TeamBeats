import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, roomHref, roomsHref, type RoomMessagePage, type RoomView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { RoomChat } from "@/features/engagement/Rooms";

/** One discussion room with live chat (spec 023 R3 US9). A room the viewer cannot read answers "not found". */
export default async function RoomPage({ params }: { params: Promise<{ id: string; roomId: string }> }) {
  const { id, roomId } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id) || !isGuid(roomId)) {
    notFound();
  }
  const room = await apiGet<RoomView>(`/api/v1/engagement/rooms/${roomId}`);
  if (!room.ok || room.data.communityId !== id) {
    notFound();
  }
  const page = await apiGet<RoomMessagePage>(`/api/v1/engagement/rooms/${roomId}/messages`);
  if (!page.ok) {
    notFound();
  }
  const t = await getTranslations("engagement");
  const locale = await getLocale();
  const community = localName(room.data.communityNames, locale);
  const name = localName(room.data.names, locale);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: community, href: communityHref(id) },
          { label: t("rooms.pageTitle"), href: roomsHref(id) },
          { label: name, href: roomHref(id, roomId) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={name} description={community} icon="chat" />
        <RoomChat initialRoom={room.data} initial={page.data} />
      </main>
    </>
  );
}
