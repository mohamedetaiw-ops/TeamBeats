// Localization gate (spec 007 T008): launch-complete locales must have every key, and every key used in code
// through t("...") must exist. Pending locales (fr, ur, hi) fall back to English until R2.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname } from "node:path";

const root = new URL("..", import.meta.url).pathname;
const launch = ["en", "ar"];

const flatten = (obj, prefix = "") =>
  Object.entries(obj).flatMap(([k, v]) => (typeof v === "object" && v !== null ? flatten(v, `${prefix}${k}.`) : [[`${prefix}${k}`, v]]));

const catalogues = Object.fromEntries(
  launch.map((l) => [l, new Map(flatten(JSON.parse(readFileSync(join(root, "messages", `${l}.json`), "utf8"))))]),
);

const errors = [];
const en = catalogues.en;
for (const locale of launch) {
  const cat = catalogues[locale];
  for (const key of en.keys()) {
    if (!cat.has(key)) errors.push(`${locale}: missing ${key}`);
    else if (!String(cat.get(key)).trim()) errors.push(`${locale}: empty ${key}`);
  }
  for (const key of cat.keys()) {
    if (!en.has(key)) errors.push(`${locale}: extra ${key} not in en`);
  }
}

// Keys used in code: t("ns.key") with getTranslations() / useTranslations() and namespace prefixes.
const files = [];
const walk = (dir) => {
  for (const name of readdirSync(dir)) {
    if (["node_modules", ".next", "scripts"].includes(name)) continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path);
    else if ([".ts", ".tsx"].includes(extname(name))) files.push(path);
  }
};
walk(root);
for (const file of files) {
  const source = readFileSync(file, "utf8");
  const ns = /(?:getTranslations|useTranslations)\(\s*"([a-z_.]+)"\s*\)/.exec(source)?.[1];
  for (const match of source.matchAll(/\bt\(\s*"([a-zA-Z_.]+)"/g)) {
    const key = ns && !match[1].includes(".") ? `${ns}.${match[1]}` : match[1];
    if (!en.has(key) && !(ns && en.has(`${ns}.${match[1]}`))) errors.push(`${file.replace(root, "")}: unknown key ${key}`);
  }
}

// Navigation labels come from the API (admin.nav.{section}).
for (const section of ["tenant_settings", "organization", "users", "roles", "plan", "identity", "connections", "audit"]) {
  if (!en.has(`admin.nav.${section}`)) errors.push(`missing admin.nav.${section}`);
}

if (errors.length) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log(`Messages OK: ${en.size} keys in ${launch.join(", ")}.`);
