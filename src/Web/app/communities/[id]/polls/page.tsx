import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, type CommunityView, type PollView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CommunityPolls } from "@/features/engagement/CommunityPolls";

/** A community's polls (spec 023 US3). The API decides who may start one, vote and see results. */
export default async function CommunityPollsPage({ params }: { params: Promise<{ id: string }> }) {
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
  const polls = await apiGet<PollView[]>(`/api/v1/engagement/communities/${id}/polls`);
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
          { label: t("polls.pageTitle"), href: `${communityHref(id)}/polls` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("polls.pageTitle")} description={name} icon="trend-up" />
        {polls.ok ? (
          <CommunityPolls community={community.data} initial={polls.data} />
        ) : (
          <StateView kind="denied" title={t("community.closedTitle")} message={t("community.joinToRead")} />
        )}
      </main>
    </>
  );
}
