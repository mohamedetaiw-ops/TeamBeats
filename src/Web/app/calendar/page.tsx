import { Suspense } from "react";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { CalendarWorkspace } from "@/features/calendar/CalendarWorkspace";

/**
 * The calendar workspace (spec 037 FR-006): agenda, week, month and team availability. Whether the manage link shows
 * is only a convenience: the API refuses event changes to anyone without the right.
 */
export default async function CalendarPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const manage = await apiGet<unknown[]>("/api/v1/calendar/admin-events");
  const t = await getTranslations("calendar");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/calendar" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="calendar" />
        <Suspense fallback={<StateView kind="loading" minBlockSize="20rem" />}>
          <CalendarWorkspace canManage={manage.ok} />
        </Suspense>
      </main>
    </>
  );
}
