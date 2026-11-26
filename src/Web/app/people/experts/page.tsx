import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ExpertsView } from "@/lib/people/skills";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ExpertFinder } from "@/features/people/Experts";
import { PeopleNav } from "@/features/people/PeopleNav";

/** Find an expert (spec 010 R2 US6 scenario 3): people who declared a matching skill, as the API ranks them. */
export default async function ExpertsPage({ searchParams }: { searchParams: Promise<{ q?: string }> }) {
  const { q } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const query = (q ?? "").slice(0, 100);
  const experts = await apiGet<ExpertsView>(`/api/v1/people/experts?q=${encodeURIComponent(query)}`);
  const t = await getTranslations("people");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/people" },
          { label: t("experts.title"), href: "/people/experts" },
        ]}
      />
      <main id="main" className="page people">
        <PageHeader title={t("experts.title")} description={t("experts.intro")} icon="star" nav={<PeopleNav current="experts" />} />
        {experts.ok ? (
          <ExpertFinder initial={experts.data} query={query} />
        ) : (
          <StateView kind="error" title={t("experts.failedTitle")} message={t("experts.failed")} />
        )}
      </main>
    </>
  );
}
