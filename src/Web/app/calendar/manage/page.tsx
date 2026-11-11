import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ManageCalendar } from "@/features/calendar/ManageCalendar";
import type { AdminEvent, Settings } from "@/lib/calendar";

/**
 * Company and department events and the calendar settings (spec 037 US5, FR-017 to FR-021). The API lists only the
 * events the person may manage and refuses settings without hr.manage; this page shows what it is given.
 */
export default async function ManageCalendarPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("calendar");
  const [events, settings] = await Promise.all([apiGet<AdminEvent[]>("/api/v1/calendar/admin-events"), apiGet<Settings>("/api/v1/calendar/settings")]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/calendar" },
          { label: t("manage.title"), href: "/calendar/manage" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("manage.title")} description={t("manage.intro")} icon="settings" />
        {!events.ok && !settings.ok ? (
          <StateView kind="error" message={t("manage.denied")} />
        ) : (
          <ManageCalendar events={events.ok ? events.data : null} settings={settings.ok ? settings.data : null} />
        )}
      </main>
    </>
  );
}
