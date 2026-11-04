import type { ReactNode } from "react";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { AdminNav } from "@/components/shell/AdminNav";

/**
 * Admin shell (spec 006 US4). Sections come from the API, which evaluates permissions; the UI never decides
 * access by itself, and hiding a link is not a security control.
 */
export default async function AdminLayout({ children }: { children: ReactNode }) {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  if (!nav.ok || nav.data.length === 0) {
    redirect("/");
  }
  const t = await getTranslations("admin");
  return (
    <>
      <Header me={me.data} />
      <div className="admin">
        <AdminNav label={t("navLabel")} sections={nav.data} />
        <main id="main" className="admin-content">
          {children}
        </main>
      </div>
    </>
  );
}
