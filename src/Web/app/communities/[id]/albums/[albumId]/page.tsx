import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { albumHref, albumsHref, communityHref, isGuid, localName, type AlbumPage, type CommunityView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { Album } from "@/features/engagement/Gallery";

/** One photo album (spec 023 US7). Which photos show, and who may upload, tag or approve, comes from the API. */
export default async function AlbumDetailPage({ params }: { params: Promise<{ id: string; albumId: string }> }) {
  const { id, albumId } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id) || !isGuid(albumId)) {
    notFound();
  }
  const [community, album] = await Promise.all([
    apiGet<CommunityView>(`/api/v1/engagement/communities/${id}`),
    apiGet<AlbumPage>(`/api/v1/engagement/albums/${albumId}`),
  ]);
  if (!community.ok || !album.ok || album.data.album.communityId !== id) {
    notFound();
  }
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
          { label: t("gallery.pageTitle"), href: albumsHref(id) },
          { label: album.data.album.title, href: albumHref(id, albumId) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={album.data.album.title} description={name} icon="image" />
        <Album initial={album.data} meId={me.data.id} />
      </main>
    </>
  );
}
