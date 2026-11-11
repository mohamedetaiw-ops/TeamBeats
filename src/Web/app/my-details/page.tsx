import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { DataChangeGroup } from "@/lib/data-changes";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { MyDetails } from "@/features/data-changes/MyDetails";

/** Asking HR to change my details (spec 022 US6). The API decides which groups are offered. */
export default async function MyDetailsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("data_changes");
  const groups = await apiGet<DataChangeGroup[]>("/api/v1/me/data-changes/groups");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/my-details" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="user" />
        {groups.ok ? <MyDetails groups={groups.data} /> : <StateView kind="empty" title={t("unavailableTitle")} message={t("unavailable")} />}
      </main>
    </>
  );
}
