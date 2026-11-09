import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { BestBet, Rebuild, SynonymSet } from "@/lib/search/types";
import { BestBets, Rebuilds, Synonyms } from "@/features/search/SearchAdmin";
import "@/features/search/search.css";

/**
 * Search tuning (spec 012 US4, FR-010, FR-012, T009): best bets, synonyms and index rebuilds. The page exists only when
 * the API lists the section; every call is authorized again by the API.
 */
export default async function SearchAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "search")) notFound();
  const [bets, synonyms, rebuilds] = await Promise.all([
    apiGet<{ bestBets: BestBet[] }>("/api/v1/search/best-bets"),
    apiGet<{ synonymSets: SynonymSet[] }>("/api/v1/search/synonyms"),
    apiGet<Rebuild[]>("/api/v1/search/rebuilds"),
  ]);
  if (!bets.ok || !synonyms.ok || !rebuilds.ok) notFound();
  const t = await getTranslations("search.admin");
  return (
    <div className="search-admin">
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <BestBets initial={bets.data.bestBets} />
      <Synonyms initial={synonyms.data.synonymSets} />
      <Rebuilds initial={rebuilds.data} />
    </div>
  );
}
