import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { CatalogueView } from "@/lib/services/types";
import type { ExitReasons, JourneySummary, JourneyTemplate } from "@/lib/journeys";
import { ExitReasonsPanel } from "@/features/journeys/ExitReasonsPanel";
import { JourneyDashboard } from "@/features/journeys/JourneyDashboard";
import { JourneyTemplates } from "@/features/journeys/JourneyTemplates";

/**
 * Journeys (spec 022 US4, US5, FR-013, FR-018): the dashboard of journeys in the officer's units, exit reasons and the
 * journey templates. The section appears only when the API lists it; when the plan lacks journeys the API refuses and
 * the page says so. Every call is authorized again by the API.
 */
export default async function JourneysAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "journeys")) {
    notFound();
  }
  const [templates, journeys, units, catalogue, exitReasons] = await Promise.all([
    apiGet<JourneyTemplate[]>("/api/v1/hr/journey-templates"),
    apiGet<JourneySummary[]>("/api/v1/hr/journeys?status=active"),
    listUnits(),
    apiGet<CatalogueView>("/api/v1/services"),
    apiGet<ExitReasons>("/api/v1/hr/exit-reasons"),
  ]);
  const t = await getTranslations("journeys.admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      {templates.ok && journeys.ok ? (
        <div className="journeys">
          <JourneyDashboard initial={journeys.data} templates={templates.data} />
          {exitReasons.ok ? <ExitReasonsPanel data={exitReasons.data} units={units.ok ? units.data : []} /> : null}
          <JourneyTemplates
            initial={templates.data}
            units={units.ok ? units.data : []}
            services={catalogue.ok ? catalogue.data.services.filter((s) => s.kind === "form").map((s) => ({ id: s.id, names: s.names })) : []}
          />
        </div>
      ) : (
        <p className="notice">{t("notInPackage")}</p>
      )}
    </>
  );
}
