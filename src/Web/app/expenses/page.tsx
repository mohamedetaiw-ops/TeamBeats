import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ExpenseForm } from "@/lib/expenses";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { MyExpenses } from "@/features/expenses/MyExpenses";

/** My expense claims (spec 022 US8): send a claim and follow it. The API holds the policy and decides every check. */
export default async function ExpensesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("expenses");
  const form = await apiGet<ExpenseForm>("/api/v1/me/expenses/form");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("pageTitle"), href: "/expenses" }]} />
      <main id="main" className="page">
        <PageHeader title={t("pageTitle")} description={t("pageIntro")} icon="files" />
        {form.ok ? <MyExpenses form={form.data} /> : <StateView kind="empty" title={t("unavailableTitle")} message={t("unavailable")} />}
      </main>
    </>
  );
}
