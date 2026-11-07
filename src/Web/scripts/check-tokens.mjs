// Token gate (spec 033 T010): styles read only the --tb-* custom properties that the token schema generates into
// app/tokens.css. Component-local layout properties use the --xp- prefix and must be declared or set by our own code.
// Any other custom property, or a --tb-* name the schema does not define, fails the build.
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, extname, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const tokensFile = join(root, "app", "tokens.css");
const defined = new Set([...readFileSync(tokensFile, "utf8").matchAll(/(--tb-[a-z0-9-]+)\s*:/g)].map((m) => m[1]));
const errors = [];

const walk = (dir, out = []) => {
  for (const name of readdirSync(dir)) {
    if (["node_modules", ".next", "scripts"].includes(name)) continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path, out);
    else if ([".css", ".ts", ".tsx"].includes(extname(name))) out.push(path);
  }
  return out;
};

for (const file of walk(root)) {
  if (file === tokensFile) continue;
  const rel = relative(root, file);
  const source = readFileSync(file, "utf8");
  for (const [, name] of source.matchAll(/(?<![\w-])(--[a-z][a-z0-9-]*[a-z0-9])(?![\w*-])/g)) {
    if (name.startsWith("--xp-")) continue;
    if (!name.startsWith("--tb-")) {
      errors.push(`${rel}: ${name} is not a theme token; use a --tb-* token from app/tokens.css or a local --xp-* property`);
    } else if (!defined.has(name)) {
      errors.push(`${rel}: ${name} is not in the token schema (app/tokens.css)`);
    } else if (extname(file) === ".css" && new RegExp(`${name}\\s*:`).test(source)) {
      errors.push(`${rel}: ${name} is set here; theme tokens come only from the tenant theme and app/tokens.css`);
    }
  }
}

if (errors.length > 0) {
  console.error(errors.join("\n"));
  process.exit(1);
}
console.log(`Token gate passed: ${defined.size} tokens.`);
