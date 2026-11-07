import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import type { Location, PersonCard } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { PeopleDirectory } from "@/features/people/PeopleDirectory";
import { PeopleNav } from "@/features/people/PeopleNav";

/** The people directory (spec 010 US2 and US4): search with unit and location filters, and new joiners. */
export default async function PeoplePage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [newcomers, locations, units] = await Promise.all([
    apiGet<PersonCard[]>("/api/v1/people/newcomers"),
    apiGet<Location[]>("/api/v1/people/locations"),
    listUnits(),
  ]);
  if (!newcomers.ok) {
    notFound();
  }
  const t = await getTranslations("people");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/people" }]} />
      <main id="main" className="page">
        <h1>{t("title")}</h1>
        <p>{t("intro")}</p>
        <PeopleNav current="directory" />
        <PeopleDirectory newcomers={newcomers.data} locations={locations.ok ? locations.data : []} units={units.ok ? units.data : []} />
      </main>
    </>
  );
}
