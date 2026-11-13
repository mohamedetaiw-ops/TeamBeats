import { getRequestConfig } from "next-intl/server";
import { cookies, headers } from "next/headers";
import { apiGet, type Me } from "@/lib/api";
import { fallbackLocale, isLocale, launchLocales, type Locale } from "./locales";
import en from "@/messages/en.json";
import { messageFallback, onIntlError } from "./fallback";

/**
 * Locale resolution mirrors the API (spec 007 FR-002): signed-in users get the locale the API resolved from their
 * preference and the tenant settings. Anonymous pages use the language cookie or the browser, limited to launch locales.
 */
async function resolveLocale(): Promise<Locale> {
  const me = await apiGet<Me>("/api/v1/me");
  if (me.ok && isLocale(me.data.locale)) {
    return me.data.locale;
  }
  const cookie = (await cookies()).get("tb_locale")?.value;
  if (isLocale(cookie) && launchLocales.includes(cookie)) {
    return cookie;
  }
  const accept = (await headers()).get("accept-language") ?? "";
  const preferred = accept.split(",").map((part) => part.split(";")[0]?.trim().slice(0, 2));
  return preferred.find((l): l is Locale => isLocale(l) && launchLocales.includes(l)) ?? fallbackLocale;
}

export default getRequestConfig(async () => {
  const locale = await resolveLocale();
  const messages = launchLocales.includes(locale)
    ? (await import(`@/messages/${locale}.json`)).default
    : en;
  return { locale, messages, getMessageFallback: messageFallback, onError: onIntlError };
});
