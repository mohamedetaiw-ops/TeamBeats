import { redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { pickName, type SiteView } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import { NewItemForm, type EventChoice } from "@/features/content/NewItemForm";

/** Starts a content item (spec 009 US1). */
export default async function NewContentItemPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const sites = await apiGet<SiteView[]>("/api/v1/content/sites");
  const t = await getTranslations("content.items");
  const locale = await getLocale();
  // Company and department events of the coming year, for an event item to link (spec 009 FR-018 R3).
  const choices = await apiGet<{ id: string; titles: Record<string, string>; start: string | null; startDate: string | null }[]>("/api/v1/content/items/event-choices");
  const events: EventChoice[] = (choices.ok ? choices.data : []).map((e) => {
    const when = e.start ?? e.startDate;
    return { id: e.id, title: pickName(e.titles, locale), when: when ? new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(when)) : "" };
  });
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
          <NewItemForm
            sites={sites.data.filter((s) => s.status === "active")}
            locales={me.data.tenant.enabledLocales}
            defaultLocale={me.data.tenant.defaultLocale}
            events={events}
          />
        ) : (
          <StateView kind="denied" title={t("denied")} message="" />
        )}
      </main>
    </>
  );
}
