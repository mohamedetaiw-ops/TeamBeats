import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { ExpenseClaim, ExpensePolicyView, ExpenseRate } from "@/lib/expenses";
import { ExpenseAdmin } from "@/features/expenses/ExpenseAdmin";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * The expense policy, exchange rates and the claims finance covers (spec 022 FR-023). The section appears only when
 * the API lists it; when the plan lacks expenses the API refuses and the page says so.
 */
export default async function ExpensesAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "expenses")) {
    notFound();
  }
  const [policy, rates, claims] = await Promise.all([
    apiGet<ExpensePolicyView>("/api/v1/hr/expense-policy"),
    apiGet<ExpenseRate[]>("/api/v1/hr/expense-rates"),
    apiGet<ExpenseClaim[]>("/api/v1/hr/expenses"),
  ]);
  const t = await getTranslations("expenses.admin");
  return (
    <>
      <AdminHeader section="expenses" description={t("intro")} />
      {policy.ok ? (
        <ExpenseAdmin policy={policy.data} rates={rates.ok ? rates.data : []} claims={claims.ok ? claims.data : []} />
      ) : (
        <p className="notice">{t("notInPackage")}</p>
      )}
    </>
  );
}
