import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/ui/PageHeader";
import { WorkspaceEntry } from "@/features/app/WorkspaceEntry";

/**
 * The store app's start (spec 030 R3 User Story 6 scenario 1, ADR-0023): the person types their workspace address and
 * the app opens that workspace's sign-in. It holds nothing about anyone; the app says here why it came back, for
 * example a workspace whose plan has no store apps.
 */
export default async function AppStartPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const t = await getTranslations("app.start");
  const raw = (await searchParams).shell;
  const reason = typeof raw === "string" && ["not_available", "unreachable", "managed_invalid"].includes(raw) ? raw : null;
  return (
    <main id="main" className="page narrow">
      <PageHeader title={t("title")} description={t("intro")} icon="globe" />
      <WorkspaceEntry problem={reason ? t(`problems.${reason}`) : null} />
    </main>
  );
}
