import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { Header } from "@/components/shell/Header";
import { NotificationCentre } from "@/features/notifications/NotificationCentre";

/** The notification centre page (spec 015 US1). */
export default async function NotificationsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("notifications");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/notifications" }]} />
      <main id="main" className="page">
        <div className="notification-page-head">
          <h1>{t("title")}</h1>
          <Link href="/notifications/preferences">{t("preferences.link")}</Link>
        </div>
        <NotificationCentre />
      </main>
    </>
  );
}
