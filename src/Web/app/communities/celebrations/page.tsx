import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { celebrationsHref, type CelebrationPreference, type CelebrationSettingsView, type CelebrationsView } from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { Celebrations } from "@/features/engagement/Celebrations";

/** Celebrations (spec 023 US6). The API decides what shows and who changes the settings. */
export default async function CelebrationsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("engagement");
  const [list, preference, settings] = await Promise.all([
    apiGet<CelebrationsView>("/api/v1/engagement/celebrations"),
    apiGet<CelebrationPreference>("/api/v1/engagement/celebrations/preferences"),
    apiGet<CelebrationSettingsView>("/api/v1/engagement/settings/celebrations"),
  ]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("pageTitle"), href: "/communities" },
          { label: t("celebrations.pageTitle"), href: celebrationsHref },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("celebrations.pageTitle")} description={t("celebrations.pageIntro")} icon="star" />
        {list.ok && preference.ok ? (
          <Celebrations initial={{ list: list.data, preference: preference.data, settings: settings.ok ? settings.data : null }} />
        ) : (
          <StateView kind={!list.ok && list.status === 403 ? "unsupported" : "error"} title={t("celebrations.unavailableTitle")} message={t("celebrations.unavailable")} />
        )}
      </main>
    </>
  );
}
