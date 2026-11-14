import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AiSummarySetting, BestBet, FederatedSource, JudgedSetSummary, QueryTextSetting, Rebuild, RetrievalConfig, SynonymSet } from "@/lib/search/types";
import type { OrgUnitView } from "@/lib/admin/types";
import { AiSummaryPanel, BestBets, FederatedSources, QueryTextPanel, Rebuilds, RetrievalSettingsPanel, Synonyms } from "@/features/search/SearchAdmin";
import { JudgedSets } from "@/features/search/JudgedSets";
import "@/features/search/search.css";

/**
 * Search tuning (spec 012 US4, FR-010, FR-012, T009): best bets, synonyms, index rebuilds and the retrieval configuration with
 * its release gate (US3 scenario 3, FR-008, T007) and the judged query sets it scores (US8), federated sources (US5) and the AI summary (US6) where the package has them. The page exists only when
 * the API lists the section; every call is authorized again by the API.
 */
export default async function SearchAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "search")) notFound();
  const [bets, synonyms, rebuilds, retrieval, federated, summary, judged, units, queryText] = await Promise.all([
    apiGet<{ bestBets: BestBet[] }>("/api/v1/search/best-bets"),
    apiGet<{ synonymSets: SynonymSet[] }>("/api/v1/search/synonyms"),
    apiGet<Rebuild[]>("/api/v1/search/rebuilds"),
    apiGet<RetrievalConfig>("/api/v1/search/retrieval-config"),
    apiGet<FederatedSource[]>("/api/v1/search/federated-sources"),
    apiGet<AiSummarySetting>("/api/v1/search/ai-summary"),
    apiGet<JudgedSetSummary[]>("/api/v1/evaluation/search-sets"),
    apiGet<OrgUnitView[]>("/api/v1/org/units"),
    apiGet<QueryTextSetting>("/api/v1/search/query-text"),
  ]);
  if (!bets.ok || !synonyms.ok || !rebuilds.ok || !retrieval.ok) notFound();
  const t = await getTranslations("search.admin");
  return (
    <div className="search-admin">
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <BestBets initial={bets.data.bestBets} />
      <Synonyms initial={synonyms.data.synonymSets} />
      <Rebuilds initial={rebuilds.data} />
      <RetrievalSettingsPanel initial={retrieval.data} />
      {judged.ok ? <JudgedSets initial={judged.data} units={units.ok ? units.data.filter((u) => u.status === "active") : []} /> : null}
      {federated.ok ? <FederatedSources initial={federated.data} /> : null}
      {summary.ok ? <AiSummaryPanel initial={summary.data} /> : null}
      {queryText.ok ? <QueryTextPanel initial={queryText.data} /> : null}
    </div>
  );
}
