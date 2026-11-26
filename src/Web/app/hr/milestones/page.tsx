import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { Milestone } from "@/lib/milestones";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { MilestoneList } from "@/features/milestones/MilestoneList";

/** Probation and contract milestones of the people an HR officer covers (spec 022 US9). The API decides who sees which. */
export default async function MilestonesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const list = await apiGet<Milestone[]>("/api/v1/hr/milestones");
  const t = await getTranslations("milestones");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/hr/milestones" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="calendar" />
        {list.ok ? <MilestoneList initial={list.data} /> : <p className="notice">{list.status === 403 ? t("denied") : t("failed")}</p>}
      </main>
    </>
  );
}
