import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { MyLeave } from "@/features/leave/MyLeave";

/** My leave (spec 022 US1): balances, the request form, my requests and replacement asks. Read in the browser so it stays live. */
export default async function LeavePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("leave");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/leave" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="calendar" />
        <MyLeave />
      </main>
    </>
  );
}
