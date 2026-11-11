import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { MyPayslips } from "@/features/payslips/MyPayslips";

/** My payslips (spec 019 US6). The list is read in the browser so the fresh sign-in can return here. */
export default async function PayslipsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("payslips");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/payslips" }]} />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="files" />
        <MyPayslips />
      </main>
    </>
  );
}
