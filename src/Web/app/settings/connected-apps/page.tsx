import { notFound, redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { ConnectedApp } from "@/lib/api-access";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { ConnectedApps } from "@/features/api-access/ConnectedApps";

/** Apps the person connected to their account (spec 035 FR-029). Without the public API in the package there is no page. */
export default async function ConnectedAppsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const apps = await apiGet<ConnectedApp[]>("/api/v1/me/connected-apps");
  if (!apps.ok) {
    notFound();
  }
  const [t, preferences] = await Promise.all([getTranslations("apiAccess.connected"), getTranslations("preferences")]);
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: preferences("title"), href: "/settings" },
          { label: t("title"), href: "/settings/connected-apps" },
        ]}
      />
      <main id="main" className="page">
        <PageHeader title={t("title")} description={t("intro")} icon="link" />
        <ConnectedApps initial={apps.data} />
      </main>
    </>
  );
}
