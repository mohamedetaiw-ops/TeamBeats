// Placement isolation gate (CLAUDE.md rule 11, spec 032 FR-014 and T014): a placement component never imports another
// placement, and only the registry names placement modules. Components talk through declared events only.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const placements = join(root, "components", "placements");
const registry = join(root, "lib", "experience", "registry.ts");
const errors = [];

const walk = (dir, out = []) => {
  for (const name of readdirSync(dir)) {
    if (["node_modules", ".next", "scripts"].includes(name)) continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path, out);
    else if ([".ts", ".tsx"].includes(extname(name))) out.push(path);
  }
  return out;
};

const imports = (source) => [...source.matchAll(/(?:from\s+|import\(\s*)["']([^"']+)["']/g)].map((m) => m[1]);

for (const file of walk(root)) {
  const rel = relative(root, file);
  for (const target of imports(readFileSync(file, "utf8"))) {
    const namesPlacement = target.includes("components/placements") || (file.startsWith(placements) && target.startsWith("./"));
    if (namesPlacement && file !== registry) {
      errors.push(`${rel} imports ${target}: only lib/experience/registry.ts may load placement code`);
    }
  }
}

if (errors.length > 0) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log("Placements OK: no placement imports another, only the registry loads them.");
