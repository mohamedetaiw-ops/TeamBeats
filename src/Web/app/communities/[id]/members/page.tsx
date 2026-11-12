import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, type CommunityView, type MemberView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CommunityMembers } from "@/features/engagement/CommunityMembers";

/** Who is in a community, and for owners, requests and roles (spec 023 US1, section 3). */
export default async function CommunityMembersPage({ params }: { params: Promise<{ id: string }> }) {
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
  const members = await apiGet<MemberView[]>(`/api/v1/engagement/communities/${id}/members`);
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
          { label: t("members.pageTitle"), href: `${communityHref(id)}/members` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("members.pageTitle")} description={name} icon="people" />
        {members.ok ? (
          <CommunityMembers community={community.data} initial={members.data} />
        ) : (
          <StateView kind="denied" title={t("community.closedTitle")} message={t("community.joinToRead")} />
        )}
      </main>
    </>
  );
}
