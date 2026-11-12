import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, type DirectoryRights, type KeywordFilterView, type ModerationItem } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { KeywordFilterEditor, ModerationQueue } from "@/features/engagement/ModerationQueue";

/**
 * The moderation queue (spec 023 US8) and, for engagement administrators, the keyword filter (FR-019). The API returns
 * only the items in communities this viewer moderates.
 */
export default async function ModerationPage({ searchParams }: { searchParams: Promise<{ communityId?: string }> }) {
  const { communityId } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("engagement");
  const scope = communityId && isGuid(communityId) ? `?communityId=${communityId}` : "";
  const [queue, rights] = await Promise.all([
    apiGet<ModerationItem[]>(`/api/v1/engagement/moderation${scope}`),
    apiGet<DirectoryRights>("/api/v1/engagement/rights"),
  ]);
  const keywords = rights.ok && rights.data.canManageKeywords ? await apiGet<KeywordFilterView>("/api/v1/engagement/settings/keywords") : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: t("moderation.pageTitle"), href: "/communities/moderation" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("moderation.pageTitle")} description={t("moderation.pageIntro")} icon="alert" />
        {queue.ok ? (
          <ModerationQueue initial={queue.data} />
        ) : (
          <StateView kind={queue.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
        {keywords?.ok ? <KeywordFilterEditor initial={keywords.data} eTag={`"k${keywords.data.version}"`} /> : null}
      </main>
    </>
  );
}
