import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { albumsHref, communityHref, isGuid, localName, type AlbumList, type CommunityView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CommunityAlbums } from "@/features/engagement/Gallery";

/** A community's photo albums (spec 023 US7). The API decides who may read them and who may make one. */
export default async function CommunityAlbumsPage({ params }: { params: Promise<{ id: string }> }) {
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
  const albums = await apiGet<AlbumList>(`/api/v1/engagement/communities/${id}/albums`);
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
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("gallery.pageTitle")} description={name} icon="image" />
        {albums.ok ? (
          <CommunityAlbums communityId={id} initial={albums.data} />
        ) : (
          <StateView kind="denied" title={t("community.closedTitle")} message={t("community.joinToRead")} />
        )}
      </main>
    </>
  );
}
