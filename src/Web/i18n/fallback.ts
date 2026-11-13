import type { IntlError } from "next-intl";

/**
 * What a screen shows for a message that is missing (spec 007 FR-030): never the raw resource key. The last part of
 * the key is written as words ("org_unit" becomes "Org unit"), which is readable while the missing message is added.
 * `npm run check:messages` fails the build for every key it can see; this covers values only the API knows.
 */
export function messageFallback({ key }: { namespace?: string; key: string; error: IntlError }): string {
  const last = key.split(".").pop() ?? key;
  const words = last.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").trim().toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Missing messages are reported in development and in tests, and never thrown at a person. */
export function onIntlError(error: IntlError): void {
  if (process.env.NODE_ENV !== "production") console.error(`[i18n] ${error.message}`);
}
