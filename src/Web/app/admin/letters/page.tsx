import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { Letter, LetterSettings, LetterType } from "@/lib/letters";
import { LetterAdmin } from "@/features/letters/LetterAdmin";

/**
 * Letter types, the verification page setting and the letters HR covers (spec 022 FR-021, FR-022). The section appears
 * only when the API lists it; when the plan lacks letters the API refuses and the page says so.
 */
export default async function LettersAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "letters")) {
    notFound();
  }
  const [types, settings, letters, units] = await Promise.all([
    apiGet<LetterType[]>("/api/v1/hr/letter-types"),
    apiGet<LetterSettings>("/api/v1/hr/letter-settings"),
    apiGet<Letter[]>("/api/v1/hr/letters"),
    listUnits(),
  ]);
  const t = await getTranslations("letters.admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      {types.ok && settings.ok ? (
        <LetterAdmin types={types.data} settings={settings.data} letters={letters.ok ? letters.data : []} units={units.ok ? units.data : []} />
      ) : (
        <p className="notice">{t("notInPackage")}</p>
      )}
    </>
  );
}
