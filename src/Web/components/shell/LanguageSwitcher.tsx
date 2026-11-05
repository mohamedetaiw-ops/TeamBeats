"use client";

import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";

/** Saves the preference through the API (spec 007 US1) and re-renders with the new language and direction. */
export function LanguageSwitcher({ label, locales, current }: { label: string; locales: string[]; current: string }) {
  const t = useTranslations("locale");
  const router = useRouter();

  async function change(locale: string) {
    await fetch("/api/v1/me/preferences", {
      method: "PUT",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ locale }),
    });
    router.refresh();
  }

  return (
    <label className="inline-field">
      <span>{label}</span>
      <select value={current} onChange={(e) => void change(e.target.value)}>
        {locales.map((l) => (
          <option key={l} value={l} lang={l}>
            {t(l)}
          </option>
        ))}
      </select>
    </label>
  );
}
