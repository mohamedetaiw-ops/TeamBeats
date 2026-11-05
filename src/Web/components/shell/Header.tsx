import { getTranslations } from "next-intl/server";
import type { Me } from "@/lib/api";
import { LanguageSwitcher } from "./LanguageSwitcher";
import { SignOutButton } from "./SignOutButton";

export async function Header({ me }: { me: Me }) {
  const t = await getTranslations();
  const enabled = me.tenant.enabledLocales.filter((l) => l === "ar" || l === "en");
  return (
    <header className="header">
      <span className="brand">{t("app.name")}</span>
      <div className="header-actions">
        <bdi>{me.displayName}</bdi>
        {enabled.length > 1 ? <LanguageSwitcher label={t("header.language")} locales={enabled} current={me.locale} /> : null}
        <SignOutButton label={t("header.signOut")} />
      </div>
    </header>
  );
}
