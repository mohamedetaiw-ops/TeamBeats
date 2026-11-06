import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

/**
 * Expected text comes from the web app's own catalogues, so tests never hard-code user-facing strings (CLAUDE.md
 * rule 8) and a wording change does not break them. Only the launch-complete locales are loaded.
 */
export type LaunchLocale = "ar" | "en";

type Catalogue = { [key: string]: string | Catalogue };

const cache = new Map<LaunchLocale, Catalogue>();

function load(locale: LaunchLocale): Catalogue {
  let catalogue = cache.get(locale);
  if (!catalogue) {
    const file = fileURLToPath(new URL(`../../../src/Web/messages/${locale}.json`, import.meta.url));
    catalogue = JSON.parse(readFileSync(file, "utf8")) as Catalogue;
    cache.set(locale, catalogue);
  }
  return catalogue;
}

/** Message for a dotted key, with ICU-style {name} placeholders filled in. */
export function msg(locale: LaunchLocale, key: string, values: Record<string, string> = {}): string {
  let node: string | Catalogue | undefined = load(locale);
  for (const part of key.split(".")) {
    node = typeof node === "object" ? node[part] : undefined;
  }
  if (typeof node !== "string") {
    throw new Error(`Missing message ${locale}:${key}`);
  }
  return node.replace(/\{(\w+)\}/g, (_, name: string) => values[name] ?? `{${name}}`);
}
