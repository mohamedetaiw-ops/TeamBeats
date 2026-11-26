import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { getPreferences } from "@/lib/shell/client";
import { Header } from "@/components/shell/Header";
import { SettingsSection } from "@/components/ui/FormField";
import { PageHeader } from "@/components/ui/PageHeader";
import { OutlookSettings } from "@/features/shell/OutlookSettings";
import { PagesReset } from "@/features/shell/PagesReset";
import { PreferencesData } from "@/features/shell/PreferencesData";
import { PreferenceControls } from "@/features/shell/SettingsPanel";
import { KioskBadgeSettings } from "@/features/kiosk/KioskBadgeSettings";
import { ProductionOptOut } from "@/features/evaluation/ProductionOptOut";
import type { ProductionOptOutView } from "@/lib/evaluation/types";
import type { BehaviorChoice as BehaviorChoiceView } from "@/lib/shell/types";
import { BehaviorChoice } from "@/features/shell/BehaviorChoice";
import { SuggestionChoice } from "@/features/assistant/SuggestionChoice";
import { ExperimentChoice } from "@/features/shell/ExperimentChoice";
import type { ExperimentChoice as ExperimentChoiceView } from "@/lib/experiments";
import type { AssistantSuggestions } from "@/lib/assistant/types";
import type { Badge } from "@/lib/kiosk";
import "@/features/shell/preferences.css";

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
  // The kiosk badge section shows only to someone who has a badge, in a package with kiosks.
  // Connected API apps show only where the package has the public API (spec 035 FR-029).
  // The evaluation opt-out shows only where the tenant samples live runs for evaluation (spec 034 US7).
  // The personalization choice shows only where the tenant turned personalization from use on (spec 008 R3).
  // The assistant suggestions choice shows only where the tenant offers suggestions (spec 014 R3).
  // The experiments choice shows only where the package has experiments (spec 021 R3).
  const [values, t, badge, connected, evaluation, behavior, suggestions, experiments] = await Promise.all([
    getPreferences(),
    getTranslations("preferences"),
    apiGet<Badge>("/api/v1/me/kiosk-badge"),
    apiGet<unknown[]>("/api/v1/me/connected-apps"),
    apiGet<ProductionOptOutView>("/api/v1/evaluation/production/opt-out"),
    apiGet<BehaviorChoiceView>("/api/v1/me/personalization/behavior"),
    apiGet<AssistantSuggestions>("/api/v1/assistant/suggestions"),
    apiGet<ExperimentChoiceView>("/api/v1/me/experiments"),
  ]);
  const locales = me.data.tenant.enabledLocales.filter((l) => l === "ar" || l === "en");
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/settings" }]} />
      <main id="main" className="page preferences">
        <PageHeader title={t("title")} description={t("intro")} icon="settings" />
        <SettingsSection id="pref-display" icon="image" title={t("display.title")} hint={t("display.hint")}>
          <PreferenceControls locales={locales} locale={me.data.locale} values={values} />
        </SettingsSection>
        <SettingsSection id="pref-notifications" icon="bell" title={t("notifications.title")} hint={t("notifications.hint")}>
          <Link className="button button-secondary" href="/notifications/preferences">
            {t("notifications.link")}
          </Link>
        </SettingsSection>
        <SettingsSection id="pref-profile" icon="user" title={t("profile.title")} hint={t("profile.hint")}>
          <Link className="button button-secondary" href="/people/me">
            {t("profile.link")}
          </Link>
        </SettingsSection>
        <SettingsSection id="pref-apps" icon="link" title={t("apps.title")} hint={t("apps.hint")}>
          <OutlookSettings unavailable={<p className="preferences-none">{t("apps.none")}</p>} />
        </SettingsSection>
        {connected.ok ? (
          <SettingsSection id="pref-connected" icon="link" title={t("connected.title")} hint={t("connected.hint", { count: connected.data.length })}>
            <Link className="button button-secondary" href="/settings/connected-apps">
              {t("connected.link")}
            </Link>
          </SettingsSection>
        ) : null}
        {badge.ok && badge.data.issued ? (
          <SettingsSection id="pref-kiosk" icon="lock" title={t("kiosk.title")} hint={t("kiosk.hint")}>
            <KioskBadgeSettings initial={badge.data} />
          </SettingsSection>
        ) : null}
        {evaluation.ok && evaluation.data.available ? (
          <SettingsSection id="pref-evaluation" icon="spark" title={t("evaluation.title")} hint={t("evaluation.hint")}>
            <ProductionOptOut initial={evaluation.data} />
          </SettingsSection>
        ) : null}
        {behavior.ok && behavior.data.available ? (
          <SettingsSection id="pref-behavior" icon="spark" title={t("behavior.title")} hint={t("behavior.hint")}>
            <BehaviorChoice initial={behavior.data} />
          </SettingsSection>
        ) : null}
        {suggestions.ok && suggestions.data.available ? (
          <SettingsSection id="pref-assistant" icon="spark" title={t("assistantSuggestions.title")} hint={t("assistantSuggestions.title_hint")}>
            <SuggestionChoice initial={suggestions.data.on} />
          </SettingsSection>
        ) : null}
        {experiments.ok && experiments.data.available ? (
          <SettingsSection id="pref-experiments" icon="trend-up" title={t("experiments.title")} hint={t("experiments.title_hint")}>
            <ExperimentChoice initial={experiments.data} />
          </SettingsSection>
        ) : null}
        <SettingsSection id="pref-pages" icon="home" title={t("pages.title")} hint={t("pages.hint")}>
          <PagesReset />
        </SettingsSection>
        <SettingsSection id="pref-data" icon="download" title={t("data.title")} hint={t("data.hint")}>
          <PreferencesData />
        </SettingsSection>
      </main>
    </>
  );
}
