import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { isGuid, learningHref, localText, sessionHref, sessionsHref, type RosterView } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { Roster } from "@/features/learning/Sessions";

/** A classroom session's roster and attendance (spec 028 R3 US10), for its facilitator and the course's administrators. */
export default async function SessionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!isGuid(id)) {
    notFound();
  }
  const roster = await apiGet<RosterView>(`/api/v1/learning/sessions/${id}/roster`);
  if (!roster.ok) {
    notFound();
  }
  const t = await getTranslations("learning");
  const title = localText(roster.data.session.courseTitles, await getLocale());
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("sessions.facilitatingTitle"), href: sessionsHref },
          { label: title, href: sessionHref(id) },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={title} eyebrow={t("sessions.eyebrow")} icon="calendar" />
        <Roster initial={roster.data} />
      </main>
    </>
  );
}
