import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { known, USE_CASES, type DebugEntry } from "@/lib/ai/types";
import { StateView } from "@/components/ui/StateView";
import { AiTabs } from "@/features/ai/AiTabs";
import { aiSection } from "@/features/ai/guard";
import "@/features/ai/ai.css";

/** What one debug window captured (spec 013 US8 scenario 2): for security officers only, as the API decides. */
export default async function AiDebugEntriesPage({ params }: { params: Promise<{ id: string }> }) {
  await aiSection();
  const { id } = await params;
  const entries = await apiGet<DebugEntry[]>(`/api/v1/ai/debug-windows/${encodeURIComponent(id)}/entries`);
  const t = await getTranslations("ai");
  const format = await getFormatter();
  if (!entries.ok && entries.status !== 403) notFound();
  return (
    <>
      <h1>{t("debug.entriesTitle")}</h1>
      <AiTabs current="debug" />
      <p>
        <Link href="/admin/ai/debug">{t("debug.back")}</Link>
      </p>
      {!entries.ok ? (
        <StateView kind="denied" title={t("debug.deniedTitle")} message={t("debug.denied")} />
      ) : entries.data.length === 0 ? (
        <StateView kind="empty" title={t("debug.noEntries")} message={t("debug.keepNote")} />
      ) : (
        <ol className="ai-route" data-testid="ai-debug-entries">
          {entries.data.map((e) => {
            const useCase = known(USE_CASES, e.useCase);
            return (
              <li key={e.id} className="ai-section ai-entry">
                <h2>
                  {format.dateTime(new Date(e.at), { dateStyle: "medium", timeStyle: "medium", timeZone: "UTC" })} ·{" "}
                  {useCase ? t(`routing.useCases.${useCase}`) : e.useCase} · <bdi dir="ltr">{e.model}</bdi>
                </h2>
                <p>{t(`policy.classification.${e.classification === "public" ? "public" : "internal"}`)}</p>
                {e.text === null ? <p>{t("debug.unreadable")}</p> : <pre dir="auto">{e.text}</pre>}
              </li>
            );
          })}
        </ol>
      )}
    </>
  );
}
