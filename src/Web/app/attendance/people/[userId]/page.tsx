import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { thisMonth } from "@/lib/attendance";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { PersonAttendance } from "@/features/attendance/PersonAttendance";

/** One person's attendance month for their manager or an HR officer covering them (spec 022 US3 scenario 5). The API decides who may see it. */
export default async function PersonAttendancePage({ params, searchParams }: { params: Promise<{ userId: string }>; searchParams: Promise<{ month?: string }> }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const { userId } = await params;
  const { month } = await searchParams;
  const t = await getTranslations("attendance");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/attendance" },
          { label: t("team.title"), href: "/attendance/team" },
          { label: t("person.title"), href: `/attendance/people/${userId}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("person.title")} description={t("person.intro")} icon="user" />
        <PersonAttendance userId={userId} initialMonth={month && /^\d{4}-\d{2}$/.test(month) ? month : thisMonth()} />
      </main>
    </>
  );
}
