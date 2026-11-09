import "server-only";
import { notFound } from "next/navigation";
import { apiGet, type NavigationSection } from "@/lib/api";

/** The evaluation pages exist only when the API lists the section; every call is authorized again by the API. */
export async function evaluationSection(): Promise<NavigationSection> {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  const section = nav.ok ? nav.data.find((s) => s.key === "evaluation") : undefined;
  if (!section) notFound();
  return section;
}
