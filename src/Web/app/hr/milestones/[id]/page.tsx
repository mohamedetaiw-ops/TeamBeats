import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { Milestone } from "@/lib/milestones";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { MilestoneDetail } from "@/features/milestones/MilestoneDetail";

/**
 * One milestone and its probation review (spec 022 US9). The API decides who may read it: the reviewer and HR officers
 * covering the person, never the person themselves.
 */
export default async function MilestonePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const milestone = await apiGet<Milestone>(`/api/v1/hr/milestones/${id}`);
  if (!milestone.ok) {
    notFound();
  }
  const t = await getTranslations("milestones");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/hr/milestones" },
          { label: milestone.data.person.displayName, href: `/hr/milestones/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t(`detailTitle.${milestone.data.kind}`, { name: milestone.data.person.displayName })} icon="calendar" />
        <MilestoneDetail initial={milestone.data} />
      </main>
    </>
  );
}
