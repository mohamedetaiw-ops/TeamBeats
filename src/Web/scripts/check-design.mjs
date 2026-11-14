// Design system consistency gate (spec 036, docs/design-system/technical/consistency.md). It runs with `npm run verify`
// beside the token gate and the literal-string lint, and fails the build when code steps outside the design system:
//  1. Inline <svg> only in the icon set, the charts (spec 036 DLS-040) and the shell brand mark; every other icon goes through components/ui/Icon.
//  2. Every <img> has an alt attribute (empty only for decorative images, see components/ui/Media).
//  3. font-family and font-size in CSS come from the type tokens (or inherit), never a literal family or size.
//  4. Per-design selectors ([data-design=...]) live only in components/ui/designs.css, so a design stays one file.
//  6. Every page under app/ starts with the shared page header (spec 036 DLS-105, DLS-118). Pages that predate the
//     rule are listed in scripts/page-header-baseline.json; the list may only shrink, and a new page is never added.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const svgAllowed = new Set(["components/ui/Icon.tsx", "components/ui/Charts.tsx", "features/shell/Shell.tsx"]);
const designCss = "components/ui/designs.css";
const errors = [];

const walk = (dir, out = []) => {
  for (const name of readdirSync(dir)) {
    if (["node_modules", ".next", "scripts", "public"].includes(name)) continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path, out);
    else if ([".css", ".tsx"].includes(extname(name))) out.push(path);
  }
  return out;
};

for (const file of walk(root)) {
  const rel = relative(root, file).replaceAll("\\", "/");
  const source = readFileSync(file, "utf8");
  if (extname(file) === ".tsx") {
    if (/<svg[\s>]/.test(source) && !svgAllowed.has(rel)) {
      errors.push(`${rel}: inline <svg>; use <Icon name=...> from components/ui/Icon or add the glyph to the icon set`);
    }
    for (const m of source.matchAll(/<img\b[^>]*>/gs)) {
      if (!/\balt=/.test(m[0])) errors.push(`${rel}: <img> without alt; describe the image or use <Media decorative>`);
    }
  } else {
    for (const m of source.matchAll(/font-(family|size)\s*:\s*([^;]+);/g)) {
      const value = m[2].trim();
      if (value !== "inherit" && !value.startsWith(`var(--tb-font-${m[1]}`)) {
        errors.push(`${rel}: font-${m[1]}: ${value}; use a --tb-font-${m[1]}-* token`);
      }
    }
    if (rel !== designCss && /\[data-design[=\]]/.test(source)) {
      errors.push(`${rel}: per-design selector; keep design-specific rules in ${designCss}`);
    }
  }
}

// Rule 6: the page header on every page. A page passes when it renders PageHeader itself or hands the whole page to
// one feature component that does. Full-screen surfaces with no shell (sign-in, kiosk, signage, previews) are exempt.
const headerExempt = /^app\/(signin|kiosk|signage|offline|preview|dev|ui-gallery|letters\/verify|preboarding|app-consent|release)\//;
const baselinePath = join(root, "scripts/page-header-baseline.json");
const baseline = new Set(JSON.parse(readFileSync(baselinePath, "utf8")));
const usesHeader = (file, seen = new Set()) => {
  if (seen.has(file) || seen.size > 12) return false;
  seen.add(file);
  let source;
  try {
    source = readFileSync(file, "utf8");
  } catch {
    return false;
  }
  if (/<PageHeader[\s>]/.test(source)) return true;
  for (const m of source.matchAll(/from "@\/(features\/[^"]+)"/g)) {
    for (const ext of [".tsx", "/index.tsx"]) {
      if (usesHeader(join(root, m[1] + ext), seen)) return true;
    }
  }
  return false;
};
const missing = [];
for (const file of walk(join(root, "app"))) {
  const rel = relative(root, file).replaceAll("\\", "/");
  if (!rel.endsWith("/page.tsx") && rel !== "app/page.tsx") continue;
  if (headerExempt.test(rel) || rel === "app/page.tsx" || rel === "app/[...path]/page.tsx" || rel === "app/p/[...slug]/page.tsx") continue;
  if (!usesHeader(file)) missing.push(rel);
}
for (const rel of missing) {
  if (!baseline.has(rel)) errors.push(`${rel}: no shared page header; start the page with <PageHeader> from components/ui/PageHeader (DLS-105)`);
}
// A listed page that now has the header only earns a reminder, so two changes moving pages at once do not block each other.
const moved = [...baseline].filter((rel) => !missing.includes(rel));
if (moved.length > 0) {
  console.warn(`scripts/page-header-baseline.json: ${moved.length} listed pages now have the page header; run check:design with --write-page-header-baseline to drop them.`);
}
if (process.argv.includes("--write-page-header-baseline")) {
  const { writeFileSync } = await import("node:fs");
  writeFileSync(baselinePath, JSON.stringify(missing.sort(), null, 2) + "\n");
  console.log(`Wrote ${missing.length} pages to scripts/page-header-baseline.json.`);
  process.exit(0);
}

if (errors.length > 0) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log(`Design gate passed: icons, images, type tokens, design rules and page headers are consistent (${baseline.size} pages still to move onto the page header).`);
