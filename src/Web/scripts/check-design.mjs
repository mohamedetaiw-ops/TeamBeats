// Design system consistency gate (spec 036, docs/design-system/technical/consistency.md). It runs with `npm run verify`
// beside the token gate and the literal-string lint, and fails the build when code steps outside the design system:
//  1. Inline <svg> only in the icon set and the shell brand mark; every other icon goes through components/ui/Icon.
//  2. Every <img> has an alt attribute (empty only for decorative images, see components/ui/Media).
//  3. font-family and font-size in CSS come from the type tokens (or inherit), never a literal family or size.
//  4. Per-design selectors ([data-design=...]) live only in components/ui/designs.css, so a design stays one file.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const svgAllowed = new Set(["components/ui/Icon.tsx", "features/shell/Shell.tsx"]);
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

if (errors.length > 0) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log("Design gate passed: icons, images, type tokens and design rules are consistent.");
