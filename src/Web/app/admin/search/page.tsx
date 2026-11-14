import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { BestBet, Rebuild, RetrievalConfig, SynonymSet } from "@/lib/search/types";
import { BestBets, Rebuilds, RetrievalSettingsPanel, Synonyms } from "@/features/search/SearchAdmin";
import "@/features/search/search.css";
import { AdminHeader } from "@/components/shell/AdminHeader";

/**
 * Search tuning (spec 012 US4, FR-010, FR-012, T009): best bets, synonyms, index rebuilds and the retrieval configuration with
 * its release gate (US3 scenario 3, FR-008, T007). The page exists only when
 * the API lists the section; every call is authorized again by the API.
 */
export default async function SearchAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "search")) notFound();
  const [bets, synonyms, rebuilds, retrieval] = await Promise.all([
    apiGet<{ bestBets: BestBet[] }>("/api/v1/search/best-bets"),
    apiGet<{ synonymSets: SynonymSet[] }>("/api/v1/search/synonyms"),
    apiGet<Rebuild[]>("/api/v1/search/rebuilds"),
    apiGet<RetrievalConfig>("/api/v1/search/retrieval-config"),
  ]);
  if (!bets.ok || !synonyms.ok || !rebuilds.ok || !retrieval.ok) notFound();
  const t = await getTranslations("search.admin");
  return (
    <div className="search-admin">
      <AdminHeader section="search" description={t("intro")} />
      <BestBets initial={bets.data.bestBets} />
      <Synonyms initial={synonyms.data.synonymSets} />
      <Rebuilds initial={rebuilds.data} />
      <RetrievalSettingsPanel initial={retrieval.data} />
    </div>
  );
}
