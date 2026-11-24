import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import { listBrands } from "@/lib/brand/client";
import { listUnits } from "@/lib/experience/client";
import { unitName } from "@/lib/experience/builder-types";
import { ThemeGovernance, type DriftReport, type TokenExtension } from "@/features/brand/ThemeGovernance";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { ButtonLink } from "@/components/ui/Button";
import "@/features/brand/brand.css";

/**
 * Theme governance (spec 033 R2 T022, US8): the drift report, the chosen brand's tokens as a DTCG file and reviewed token
 * extensions. Each part shows what the API answers for this person; the page decides nothing about access.
 */
export default async function ThemeGovernancePage({ searchParams }: { searchParams: Promise<{ brand?: string }> }) {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "branding")) {
    notFound();
  }
  const [report, brands, units, extensions] = await Promise.all([
    apiGet<DriftReport>("/api/v1/experience/drift"),
    listBrands(),
    listUnits(),
    apiGet<{ available: boolean; items: TokenExtension[] }>("/api/v1/experience/token-extensions?scope=tenant"),
  ]);
  if (!report.ok) {
    notFound();
  }
  const { brand: requested } = await searchParams;
  const chosen = brands.ok
    ? (brands.data.find((b) => b.id === requested) ?? brands.data.find((b) => b.assignedScopes.includes("tenant")) ?? brands.data[0] ?? null)
    : null;
  const t = await getTranslations("brand.governance");
  const locale = await getLocale();
  const scopes = [
    ...(extensions.ok ? [{ value: "tenant", label: t("tenant") }] : []),
    ...(units.ok ? units.data.filter((u) => u.status === "active").map((u) => ({ value: u.id, label: unitName(u, locale) })) : []),
  ];
  return (
    <>
      <AdminHeader
        section="branding"
        title={t("title")}
        description={t("intro")}
        actions={
          <ButtonLink href={chosen ? `/admin/branding?brand=${encodeURIComponent(chosen.id)}` : "/admin/branding"} variant="secondary" icon="chevron-back">
            {t("back")}
          </ButtonLink>
        }
      />
      <ThemeGovernance
        report={report.data}
        brand={chosen ? { id: chosen.id, name: chosen.name } : null}
        scopes={scopes}
        initialScope={scopes[0]?.value ?? "tenant"}
        initialExtensions={extensions.ok ? extensions.data.items : []}
        extensionsAvailable={extensions.ok ? extensions.data.available : false}
      />
    </>
  );
}
