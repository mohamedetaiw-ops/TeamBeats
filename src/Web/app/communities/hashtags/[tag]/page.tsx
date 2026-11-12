import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { hashtagHref, localName, type CommunityView, type HashtagPage } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PostList } from "@/features/engagement/PostList";

/** Posts with one hashtag, from every community this viewer can read (spec 023 US1 scenario 3). */
export default async function HashtagPage({ params }: { params: Promise<{ tag: string }> }) {
  const { tag } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const decoded = decodeURIComponent(tag);
  const [page, communities] = await Promise.all([
    apiGet<HashtagPage>(`/api/v1/engagement/hashtags/${encodeURIComponent(decoded)}`),
    apiGet<CommunityView[]>("/api/v1/engagement/communities"),
  ]);
  if (!page.ok) {
    notFound();
  }
  const t = await getTranslations("engagement");
  const locale = await getLocale();
  const names = Object.fromEntries((communities.ok ? communities.data : []).map((c) => [c.id, localName(c.names, locale)]));
  const title = t("hashtag.title", { tag: page.data.tag });
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: title, href: hashtagHref(page.data.tag) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={title} description={t("hashtag.intro")} icon="search" />
        <PostList initial={page.data.items} communityNames={names} />
      </main>
    </>
  );
}
