import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { Journey } from "@/lib/journeys";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { JourneyDetail } from "@/features/journeys/JourneyDetail";

/**
 * One journey (spec 022 US4, US5). The API decides who may read it (the person, the buddy, anyone with a task in it and
 * HR officers of the person's unit) and checks each step's assignment again when it is marked done.
 */
export default async function JourneyPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const journey = await apiGet<Journey>(`/api/v1/hr/journeys/${id}`);
  if (!journey.ok) {
    notFound();
  }
  const t = await getTranslations("journeys.page");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/hr/journeys" },
          { label: journey.data.journey.person.displayName, href: `/hr/journeys/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("detailTitle", { name: journey.data.journey.person.displayName })} icon="tasks" />
        <JourneyDetail initial={journey.data} />
      </main>
    </>
  );
}
