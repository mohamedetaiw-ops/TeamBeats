import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import type { SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { NewItemForm } from "@/features/content/NewItemForm";

/** Starts a content item (spec 009 US1). */
export default async function NewContentItemPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const sites = await apiGet<SiteView[]>("/api/v1/content/sites");
  const t = await getTranslations("content.items");
  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("listTitle"), href: "/content/items" },
          { label: t("new"), href: "/content/items/new" },
        ]}
      />
      <main id="main" className="page content-manage">
        <h1>{t("new")}</h1>
        {sites.ok && sites.data.length > 0 ? (
          <NewItemForm sites={sites.data.filter((s) => s.status === "active")} locales={me.data.tenant.enabledLocales} defaultLocale={me.data.tenant.defaultLocale} />
        ) : (
          <StateView kind="denied" title={t("denied")} message="" />
        )}
      </main>
    </>
  );
}
