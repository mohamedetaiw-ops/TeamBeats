import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type NavigationSection } from "@/lib/api";
import type { AuditPage, AuditRetention } from "@/lib/admin/types";
import { AdminHeader } from "@/components/shell/AdminHeader";
import { AuditAdmin } from "@/features/admin/AuditAdmin";

/**
 * Audit log (spec 006 US1 to US3). The section appears only when the API lists it; the entries are those the API
 * returns for the administrator's scope (AUD-004), and export and retention are authorized again by the API.
 */
export default async function AuditLogPage({ searchParams }: { searchParams: Promise<{ action?: string }> }) {
  const { action } = await searchParams;
  const initialAction = typeof action === "string" && /^[a-z0-9_.]{1,80}$/.test(action) ? action : "";
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  const section = nav.ok ? nav.data.find((s) => s.key === "audit") : undefined;
  if (!section) {
    notFound();
  }
  const [events, retention] = await Promise.all([apiGet<AuditPage>(`/api/v1/audit/events?limit=50${initialAction ? `&action=${encodeURIComponent(initialAction)}` : ""}`), apiGet<AuditRetention>("/api/v1/audit/retention")]);
  if (!events.ok) {
    notFound();
  }
  const t = await getTranslations("admin");
  return (
    <>
      <AdminHeader section="audit" description={t(section.wholeTenant ? "audit.description" : "audit.descriptionScoped")} />
      <AuditAdmin initial={events.data} retention={retention.ok ? retention.data : null} initialAction={initialAction} />
    </>
  );
}
