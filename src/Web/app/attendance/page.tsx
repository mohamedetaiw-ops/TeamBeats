import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { MyAttendance } from "@/features/attendance/MyAttendance";

/** My attendance (spec 022 US3): sign in and out, and my month with hours and exceptions. Read in the browser so it stays live. */
export default async function AttendancePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("attendance");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/attendance" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="clock" />
        <MyAttendance />
      </main>
    </>
  );
}
