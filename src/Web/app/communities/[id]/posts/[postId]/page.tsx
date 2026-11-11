import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, postHref, type CommunityView, type PostView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PostList } from "@/features/engagement/PostList";

/** One post with its comments open, where mention and comment notices and search results lead (spec 023 US1). */
export default async function CommunityPostPage({ params }: { params: Promise<{ id: string; postId: string }> }) {
  const { id, postId } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id) || !isGuid(postId)) {
    notFound();
  }
  const [community, post] = await Promise.all([apiGet<CommunityView>(`/api/v1/engagement/communities/${id}`), apiGet<PostView>(`/api/v1/engagement/posts/${postId}`)]);
  if (!community.ok || !post.ok || post.data.communityId !== id) {
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
          { label: t("post.pageTitle"), href: postHref(id, postId) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("post.pageTitle")} description={name} icon="chat" />
        <PostList initial={[post.data]} communityNames={{ [id]: name }} openComments />
      </main>
    </>
  );
}
