import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { communityHref, isGuid, localName, mirrorsHref, type CommunityView, type MirrorsView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { MirrorSettings } from "@/features/engagement/Mirrors";

/** Mirroring a community to Slack and Teams channels (spec 023 R3 US10). Only engagement administrators reach the settings. */
export default async function CommunityMirrorsPage({ params }: { params: Promise<{ id: string }> }) {
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
  const mirrors = await apiGet<MirrorsView>(`/api/v1/engagement/communities/${id}/mirrors`);
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
          { label: t("mirrors.pageTitle"), href: mirrorsHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("mirrors.pageTitle")} description={name} icon="link" />
        {mirrors.ok ? (
          <MirrorSettings communityId={id} initial={mirrors.data} />
        ) : (
          <StateView kind="denied" title={t("mirrors.deniedTitle")} message={t("mirrors.denied")} />
        )}
      </main>
    </>
  );
}
