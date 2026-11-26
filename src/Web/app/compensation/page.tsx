import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { MyCompensation } from "@/features/compensation/MyCompensation";

/** My compensation (spec 019 R3 US10). The statement is read in the browser so the fresh sign-in can return here. */
export default async function CompensationPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("compensation");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/compensation" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="files" />
        <MyCompensation />
      </main>
    </>
  );
}
