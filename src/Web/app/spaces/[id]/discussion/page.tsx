import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { CommunityView, FeedPage } from "@/lib/engagement";
import { isGuid, spaceDiscussionHref, spaceHref, spacesApi, spacesHref, type SpaceView } from "@/lib/spaces";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CommunityFeed } from "@/features/engagement/CommunityFeed";
import { SpaceNav } from "@/features/spaces/parts";

/** A space's discussion (spec 029 FR-006): a spec 023 channel whose members are the space's members. */
export default async function SpaceDiscussionPage({ params }: { params: Promise<{ id: string }> }) {
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
  const communityId = space.data.communityId;
  const [community, feed] = communityId
    ? await Promise.all([
        apiGet<CommunityView>(`/api/v1/engagement/communities/${communityId}`),
        apiGet<FeedPage>(`/api/v1/engagement/communities/${communityId}/posts`),
      ])
    : [null, null];
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: spacesHref },
          { label: space.data.name, href: spaceHref(id) },
          { label: t("discussionTitle"), href: spaceDiscussionHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("discussionTitle")} eyebrow={space.data.name} icon="chat"
          nav={<SpaceNav id={id} current="discussion" settings={space.data.rights.owns || space.data.rights.manages} files={!!space.data.libraryId} discussion={!!communityId} />} />
        {community?.ok ? (
          <CommunityFeed initial={community.data} feed={feed?.ok ? feed.data : null} locales={me.data.tenant.enabledLocales} />
        ) : !space.data.rights.reads ? (
          <StateView kind="denied" title={t("privateTitle")} message={t("private")} />
        ) : (
          <StateView kind="empty" title={t("discussionUnavailableTitle")} message={t("discussionUnavailable")} />
        )}
      </main>
    </>
  );
}
