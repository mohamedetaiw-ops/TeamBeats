import type { ReactNode } from "react";
import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getPreferences } from "@/lib/shell/client";
import { Header } from "@/components/shell/Header";
import { Icon, type IconName } from "@/components/ui/Icon";
import { PageHeader } from "@/components/ui/PageHeader";
import { OutlookSettings } from "@/features/shell/OutlookSettings";
import { PagesReset } from "@/features/shell/PagesReset";
import { PreferenceControls } from "@/features/shell/SettingsPanel";
import "@/features/shell/preferences.css";

/** One section of the page: a heading and one line of help on the start side, the controls on the end side. */
function Section({ id, icon, title, hint, children }: { id: string; icon: IconName; title: string; hint: string; children: ReactNode }) {
  return (
    <section className="preferences-section" aria-labelledby={id}>
      <div className="preferences-section-head">
        <span className="ui-icon-tile" aria-hidden="true">
          <Icon name={icon} size={18} />
        </span>
        <div>
          <h2 id={id}>{title}</h2>
          <p>{hint}</p>
        </div>
      </div>
      <div className="preferences-section-body">{children}</div>
    </section>
  );
}

/**
 * Every personal preference in one page (spec 008 FR-010, FR-027; spec 018 FR-013; spec 036 DLS-047): display, the
 * person's notifications and profile, connected apps such as Outlook, and resetting arranged pages. The quick
 * preferences popover in the shell links here.
 */
export default async function PreferencesPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [values, t] = await Promise.all([getPreferences(), getTranslations("preferences")]);
  const locales = me.data.tenant.enabledLocales.filter((l) => l === "ar" || l === "en");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/settings" }]} />
      <main id="main" className="page preferences">
        <PageHeader title={t("title")} description={t("intro")} icon="settings" />
        <Section id="pref-display" icon="image" title={t("display.title")} hint={t("display.hint")}>
          <PreferenceControls locales={locales} locale={me.data.locale} values={values} />
        </Section>
        <Section id="pref-notifications" icon="bell" title={t("notifications.title")} hint={t("notifications.hint")}>
          <Link className="button button-secondary" href="/notifications/preferences">
            {t("notifications.link")}
          </Link>
        </Section>
        <Section id="pref-profile" icon="user" title={t("profile.title")} hint={t("profile.hint")}>
          <Link className="button button-secondary" href="/people/me">
            {t("profile.link")}
          </Link>
        </Section>
        <Section id="pref-apps" icon="link" title={t("apps.title")} hint={t("apps.hint")}>
          <OutlookSettings unavailable={<p className="preferences-none">{t("apps.none")}</p>} />
        </Section>
        <Section id="pref-pages" icon="home" title={t("pages.title")} hint={t("pages.hint")}>
          <PagesReset />
        </Section>
      </main>
    </>
  );
}
