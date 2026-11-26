import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, sentimentHref, type CommunitySentimentView, type CommunityView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CommunitySentiment } from "@/features/engagement/Sentiment";

/** A community's sentiment by week (spec 023 R3 US11 scenario 3). Only its moderators and owners reach it; the API decides. */
export default async function CommunitySentimentPage({ params }: { params: Promise<{ id: string }> }) {
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
  const sentiment = await apiGet<CommunitySentimentView>(`/api/v1/engagement/communities/${id}/sentiment`);
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
          { label: t("sentiment.pageTitle"), href: sentimentHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("sentiment.pageTitle")} description={name} icon="trend-up" />
        {sentiment.ok ? (
          <CommunitySentiment view={sentiment.data} />
        ) : (
          <StateView kind="denied" title={t("sentiment.deniedTitle")} message={t("sentiment.denied")} />
        )}
      </main>
    </>
  );
}
