import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { Letter } from "@/lib/letters";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { LetterDetail } from "@/features/letters/LetterDetail";

/**
 * One HR letter (spec 022 US7). The API decides who may read it; a task ID from the inbox shows the decision panel,
 * and the API checks the assignment again when HR decides.
 */
export default async function LetterPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ task?: string }> }) {
  const { id } = await params;
  const { task } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  if (!/^[0-9a-f-]{36}$/i.test(id)) {
    notFound();
  }
  const letter = await apiGet<Letter>(`/api/v1/hr/letters/${id}`);
  if (!letter.ok) {
    notFound();
  }
  const t = await getTranslations("letters");
  const taskId = task && /^[0-9a-f-]{36}$/i.test(task) ? task : null;
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/letters" },
          { label: letter.data.reference, href: `/letters/${id}` },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("detail.title")} icon="files" />
        <LetterDetail initial={letter.data} taskId={taskId} />
      </main>
    </>
  );
}
