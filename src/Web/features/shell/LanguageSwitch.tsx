"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";

/**
 * One press between the tenant's two launch languages (spec 007), next to the settings that also hold the full list.
 * The choice is saved on the user account and the page renders again in the new language and direction.
 */
export function LanguageSwitch({ locales, locale }: { locales: string[]; locale: string }) {
  const t = useTranslations("shell");
  const tl = useTranslations("locale");
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const next = locales.find((l) => l !== locale);
  if (locales.length !== 2 || !next) {
    return null;
  }
  async function change() {
    setBusy(true);
    try {
      await fetch("/api/v1/me/preferences", { method: "PUT", headers: { "content-type": "application/json" }, body: JSON.stringify({ locale: next }) });
      router.refresh();
    } finally {
      setBusy(false);
    }
  }
  return (
    <button type="button" className="button button-quiet shell-language" aria-label={t("switchLanguage", { language: tl(next) })}
      aria-busy={busy || undefined} onClick={() => void change()}>
      <span lang={next}>{tl(next)}</span>
    </button>
  );
}
