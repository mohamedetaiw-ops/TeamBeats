import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { learningHref, sessionsHref, type SessionView } from "@/lib/learning";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Facilitating } from "@/features/learning/Sessions";

/** Classroom sessions the viewer facilitates (spec 028 R3 US10). The API lists only their own. */
export default async function FacilitatingPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("learning");
  const sessions = await apiGet<SessionView[]>("/api/v1/learning/sessions/facilitating");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: learningHref },
          { label: t("sessions.facilitatingTitle"), href: sessionsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("sessions.facilitatingTitle")} description={t("sessions.facilitatingIntro")} icon="calendar" />
        {sessions.ok ? (
          <Facilitating sessions={sessions.data} />
        ) : (
          <StateView kind={sessions.status === 403 ? "unsupported" : "error"} title={t("unavailableTitle")} message={t("unavailable")} />
        )}
      </main>
    </>
  );
}
