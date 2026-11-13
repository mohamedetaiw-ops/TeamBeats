import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ExternalAction } from "@/lib/agents/external";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { ExternalActionCard } from "@/features/agents/ExternalActionCard";

// The action and its state change while the person looks at it, so it is read on every request.
export const dynamic = "force-dynamic";

/**
 * An action an external AI client asked to do for the person (spec 035 US3 scenario 5). The notification and the task
 * inbox link here; only the person the client acted for can open it.
 */
export default async function AiActionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect(`/signin?returnTo=${encodeURIComponent(`/ai-actions/${id}`)}`);
  }
  const action = await apiGet<ExternalAction>(`/api/v1/me/ai-actions/${encodeURIComponent(id)}`);
  if (!action.ok) {
    notFound();
  }
  const [t, services] = await Promise.all([getTranslations("aiActions"), getTranslations("services")]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: services("inbox.pageTitle"), href: "/tasks" },
          { label: t("title"), href: `/ai-actions/${id}` },
        ]}
      />
      <main id="main" className="page narrow">
        <PageHeader title={t("title")} description={t("description")} icon="spark" />
        <ExternalActionCard initial={action.data} />
      </main>
    </>
  );
}
