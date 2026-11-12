import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { CommunityView, DirectoryRights } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CommunityDirectory } from "@/features/engagement/CommunityDirectory";

/** The communities directory (spec 023 US1). The API decides which communities this viewer sees and may join. */
export default async function CommunitiesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("engagement");
  const [list, rights] = await Promise.all([apiGet<CommunityView[]>("/api/v1/engagement/communities"), apiGet<DirectoryRights>("/api/v1/engagement/rights")]);
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("pageTitle"), href: "/communities" }]} />
      <main id="main" className="page">
        <PageHeader title={t("pageTitle")} description={t("pageIntro")} icon="chat" />
        {list.ok ? (
          <CommunityDirectory initial={list.data} canCreate={rights.ok && rights.data.canCreate} locales={me.data.tenant.enabledLocales} />
        ) : (
          <StateView kind={list.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
