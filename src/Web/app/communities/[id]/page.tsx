import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, type CommunityView, type FeedPage } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { CommunityFeed } from "@/features/engagement/CommunityFeed";

/**
 * One community (spec 023 US1). A closed community the viewer is not in answers "not found" from the API, so the page
 * does too. An approval community shows its name to everyone but its posts only to members.
 */
export default async function CommunityPage({ params }: { params: Promise<{ id: string }> }) {
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
  const feed = await apiGet<FeedPage>(`/api/v1/engagement/communities/${id}/posts`);
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
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={name} icon="chat" />
        <CommunityFeed initial={community.data} feed={feed.ok ? feed.data : null} locales={me.data.tenant.enabledLocales} />
      </main>
    </>
  );
}
