import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { Preferences } from "@/lib/notifications/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { NotificationPreferences } from "@/features/notifications/NotificationPreferences";

/** The employee's notification preferences page (spec 015 US2, US3). */
export default async function NotificationPreferencesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [prefs, t] = await Promise.all([apiGet<Preferences>("/api/v1/me/notification-preferences"), getTranslations("notifications")]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/notifications" },
          { label: t("preferences.title"), href: "/notifications/preferences" },
        ]}
      />
      <main id="main" className="page">
        <h1>{t("preferences.title")}</h1>
        {prefs.ok ? <NotificationPreferences initial={prefs.data} /> : <StateView kind="error" title={t("failed")} message="" />}
      </main>
    </>
  );
}
