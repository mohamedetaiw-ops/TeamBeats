import "server-only";
import { notFound } from "next/navigation";
import { apiGet, type NavigationSection } from "@/lib/api";

/** The Connections pages exist only when the API lists the section; every call is authorized again by the API. */
export async function connectionsSection(): Promise<NavigationSection> {
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  const section = nav.ok ? nav.data.find((s) => s.key === "connections") : undefined;
  if (!section) notFound();
  return section;
}
