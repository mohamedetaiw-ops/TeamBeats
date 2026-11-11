import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { categoryName, type ExpenseClaim, type ExpenseForm } from "@/lib/expenses";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { ExpenseDetail } from "@/features/expenses/ExpenseDetail";

/**
 * One expense claim (spec 022 US8). The API decides who may read it; a task ID from the inbox shows the decision
 * panel, and the API checks the assignment again when someone decides.
 */
export default async function ExpensePage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const [claim, form] = await Promise.all([apiGet<ExpenseClaim>(`/api/v1/hr/expenses/${id}`), apiGet<ExpenseForm>("/api/v1/me/expenses/form")]);
  if (!claim.ok) {
    notFound();
  }
  const t = await getTranslations("expenses");
  const locale = await getLocale();
  const names = Object.fromEntries((form.ok ? form.data.categories : []).map((c) => [c.key, categoryName(c.names, locale, c.key)]));
  const taskId = task && /^[0-9a-f-]{36}$/i.test(task) ? task : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/expenses" },
          { label: claim.data.reference, href: `/expenses/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("detail.title")} icon="files" />
        <ExpenseDetail initial={claim.data} taskId={taskId} names={names} />
      </main>
    </>
  );
}
