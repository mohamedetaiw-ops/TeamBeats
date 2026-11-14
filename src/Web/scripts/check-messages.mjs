// Localization gate (spec 007 T008): launch-complete locales must have every key, and every key used in code
// through t("...") must exist. Pending locales (fr, ur, hi) fall back to English until R2.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
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
  const ns = /(?:getTranslations|useTranslations)\(\s*"([A-Za-z_.]+)"\s*\)/.exec(source)?.[1];
  for (const match of source.matchAll(/\bt\(\s*"([a-zA-Z_.]+)"/g)) {
    const key = ns && !match[1].includes(".") ? `${ns}.${match[1]}` : match[1];
    if (!en.has(key) && !(ns && en.has(`${ns}.${match[1]}`))) errors.push(`${file.replace(root, "")}: unknown key ${key}`);
  }
}

// Navigation labels come from the API (admin.nav.{section}).
for (const section of ["tenant_settings", "organization", "users", "roles", "plan", "identity", "connections", "audit"]) {
  if (!en.has(`admin.nav.${section}`)) errors.push(`missing admin.nav.${section}`);
}

// Plan names come from the capability catalogue (spec 005 US3, DLS-068): every key seeded by an Entitlements
// migration needs a name and an area label, so the Plan page never shows a raw key.
const migrations = join(root, "..", "Modules", "Entitlements", "Migrations");
const capabilityKeys = new Set();
for (const name of readdirSync(migrations).filter((n) => n.endsWith(".sql"))) {
  const sql = readFileSync(join(migrations, name), "utf8");
  for (const match of sql.matchAll(/\(\s*'([a-z0-9_]+\.[a-z0-9_.]+)'\s*,\s*'(?:flag|limit)'/g)) capabilityKeys.add(match[1]);
}
if (capabilityKeys.size === 0) errors.push("no capability keys found in the Entitlements migrations");
for (const key of capabilityKeys) {
  if (!en.has(`admin.plan.capabilities.${key.replaceAll(".", "_")}`)) errors.push(`missing admin.plan.capabilities.${key.replaceAll(".", "_")}`);
  const area = key.split(".")[0];
  if (!en.has(`admin.areas.${area}`)) errors.push(`missing admin.areas.${area}`);
}

// Values the API returns that a screen turns into a message key (spec 007 FR-030): every constant of the backend class
// needs a message, so a new value fails here instead of showing a raw key such as documents.libraries.scope.space.
const valueLists = [
  { file: "Documents/Model.cs", type: "LibraryScopes", prefix: "documents.libraries.scope", skip: ["SpaceResource"] },
];
for (const list of valueLists) {
  const source = readFileSync(join(root, "..", "Modules", list.file), "utf8");
  const body = new RegExp(`static class ${list.type}\\s*\\{([\\s\\S]*?)\\n\\}`).exec(source)?.[1];
  if (!body) {
    errors.push(`value list ${list.type} not found in ${list.file}`);
    continue;
  }
  for (const match of body.matchAll(/const string (\w+) = "([a-z0-9_]+)";/g)) {
    if (list.skip.includes(match[1])) continue;
    if (!en.has(`${list.prefix}.${match[2]}`)) errors.push(`missing ${list.prefix}.${match[2]} (${list.type}.${match[1]})`);
  }
}

// Stored keys administrators see (spec 006 FR-021, DLS-093): every permission of the catalogue and every design token
// needs a name, so Roles and access and Brand never show a raw key. Audit actions without their own name fall back to
// their area, so every area prefix needs an area name.
const permissionSource = readFileSync(join(root, "..", "BuildingBlocks", "Authorization.Contracts", "Permissions.cs"), "utf8");
const permissionKeys = [...permissionSource.matchAll(/const string \w+ = "([a-z_]+\.[a-z_]+)";/g)].map((m) => m[1]);
if (permissionKeys.length === 0) errors.push("no permission keys found in Permissions.cs");
for (const key of permissionKeys) {
  if (!en.has(`admin.permissions.${key}`)) errors.push(`missing admin.permissions.${key}`);
}
const tokenSource = readFileSync(join(root, "..", "Modules", "Experience", "Themes", "Tokens.cs"), "utf8");
const tokenKeys = new Set([...tokenSource.matchAll(/\b[A-Z]\w*\("((?:color|font|space|radius|border|shadow|motion|state|layout|design)\.[a-z0-9_.]+[a-z0-9])"/g)].map((m) => m[1]));
if (tokenKeys.size === 0) errors.push("no design tokens found in Tokens.cs");
for (const key of tokenKeys) {
  if (!en.has(`brand.tokenNames.${key}`)) errors.push(`missing brand.tokenNames.${key}`);
}
for (const key of permissionKeys) {
  const area = key.split(".")[0];
  if (!en.has(`admin.auditAreas.${area}`) && !en.has(`admin.areas.${area}`)) errors.push(`missing admin.auditAreas.${area}`);
}

if (errors.length) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log(`Messages OK: ${en.size} keys in ${launch.join(", ")}.`);
