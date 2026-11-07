import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { Location, Policy } from "@/lib/people/types";
import { PeoplePolicyAdmin } from "@/features/people/PeoplePolicyAdmin";

/**
 * People directory administration (spec 010 US1): field sources and visibility, tenant fields, the new joiner period,
 * the location attribute and location names. The section appears only when the API lists it.
 */
export default async function PeopleAdminPage() {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || !nav.data.some((s) => s.key === "people")) {
    notFound();
  }
  const [policy, locations] = await Promise.all([apiGet<Policy>("/api/v1/people/policy"), apiGet<Location[]>("/api/v1/people/locations")]);
  if (!policy.ok) {
    notFound();
  }
  const t = await getTranslations("people.admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p className="notice">{t("intro")}</p>
      <PeoplePolicyAdmin initialPolicy={policy.data} initialLocations={locations.ok ? locations.data : []} />
    </>
  );
}
