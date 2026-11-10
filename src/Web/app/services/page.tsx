import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { CatalogueView } from "@/lib/services/types";
import { Header } from "@/components/shell/Header";
import { ServiceCatalogue } from "@/features/services/ServiceCatalogue";
import { ServicesNav } from "@/features/services/ServicesNav";
import { PageHeader } from "@/components/ui/PageHeader";

/** The service catalogue (spec 016 US1): every service this person may use, from forms, linked apps and modules. */
export default async function ServicesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const view = await apiGet<CatalogueView>("/api/v1/services");
  if (!view.ok) {
    notFound();
  }
  const t = await getTranslations("services");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("catalogue.title"), href: "/services" }]} />
      <main id="main" className="page">
        <PageHeader title={t("catalogue.title")} description={t("catalogue.intro")} icon="services" nav={<ServicesNav current="catalogue" />} />
        <ServiceCatalogue initial={view.data} />
      </main>
    </>
  );
}
