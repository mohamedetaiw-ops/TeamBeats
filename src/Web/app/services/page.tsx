import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { CatalogueView } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { ServiceCatalogue } from "@/features/services/ServiceCatalogue";
import { ServicesNav } from "@/features/services/ServicesNav";
import { PageHeader } from "@/components/ui/PageHeader";

/** The service catalogue (spec 016 US1): every service this person may use, from forms, linked apps and modules. */
export default async function ServicesPage({ searchParams }: { searchParams: Promise<{ q?: string; category?: string }> }) {
  const { q = "", category = "" } = await searchParams;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  // The search and category come from the address, so a reload or a shared link shows the same list (spec 008 FR-065).
  // The category is a tab over the services the search returned, so every tab keeps its count.
  const params = new URLSearchParams();
  if (q.trim()) params.set("q", q.trim());
  const view = await apiGet<CatalogueView>(`/api/v1/services${params.size ? `?${params.toString()}` : ""}`);
  if (!view.ok) {
    notFound();
  }
  const t = await getTranslations("services");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("catalogue.title"), href: "/services" }]} />
      <main id="main" className="page">
        <PageHeader title={t("catalogue.title")} description={t("catalogue.intro")} icon="services" nav={<ServicesNav current="catalogue" />} />
        <ServiceCatalogue initial={view.data} initialQuery={q} initialCategory={category} />
      </main>
    </>
  );
}
