// Quarterly design audit report (spec 036 R2 T013, docs/design-system/audit.md). It reads the code, not a tenant, and
// writes the part of the audit a script can count: the design debt the gates still allow (pages without the shared
// page header, own tab strips and filter bars, placements off the Widget frame, feature classes on the Studio surface
// list), the gallery coverage of the shared components and the accessibility exclusions with their review dates.
// The live screen pass and the drift report of the brand manager (spec 033 T022) are added by hand, as the guide says.
//
//   node scripts/design-audit.mjs [output.md] [--date=YYYY-MM-DD]
//
// Without an output path the report goes to standard output. The numbers only go down over time: each gate fails a
// new entry, so a rise means a gate was loosened and the report says so.
import { readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const web = fileURLToPath(new URL("..", import.meta.url));
const repo = join(web, "..", "..");
const read = (path) => readFileSync(path, "utf8");
const args = process.argv.slice(2);
const output = args.find((a) => !a.startsWith("--"));
const dateArg = args.find((a) => a.startsWith("--date="))?.slice("--date=".length);
const date = dateArg ?? new Date().toISOString().slice(0, 10);
if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) throw new Error(`--date must be YYYY-MM-DD, not ${date}`);
const quarter = `${date.slice(0, 4)} Q${Math.floor((Number(date.slice(5, 7)) - 1) / 3) + 1}`;

const walk = (dir, test, out = []) => {
  for (const name of readdirSync(dir)) {
    if (["node_modules", ".next"].includes(name)) continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path, test, out);
    else if (test(name)) out.push(path);
  }
  return out;
};
const rel = (path) => relative(web, path).replaceAll("\\", "/");

/** The names inside `new Set([...])` assigned to a constant in the design gate. */
function setNamed(source, name) {
  const match = source.match(new RegExp(`const ${name} = new Set\\(\\[([^\\]]*)\\]\\)`));
  return match ? [...match[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]) : [];
}

const gate = read(join(web, "scripts", "check-design.mjs"));
const pages = walk(join(web, "app"), (n) => n === "page.tsx").map(rel);
const headerDebt = JSON.parse(read(join(web, "scripts", "page-header-baseline.json")));
const ownStrips = setNamed(gate, "ownStrips");
const ownFrame = setNamed(gate, "ownFrame");

// Feature classes on the Studio surface list: every class in the Studio surfaces block that is not a shared ui- part.
const designs = read(join(web, "components", "ui", "designs.css"));
const studioStart = designs.indexOf("*/", designs.indexOf("Studio surfaces")) + 2;
const studioBlock = designs.slice(studioStart, designs.indexOf("{", studioStart));
const studioFeature = [...new Set([...studioBlock.matchAll(/\.([a-z][a-z0-9-]*)/g)].map((m) => m[1]))].filter((c) => !c.startsWith("ui-")).sort();

// Gallery coverage: the shared components and the ones the gallery imports.
const components = walk(join(web, "components", "ui"), (n) => n.endsWith(".tsx")).map((p) => rel(p).replace(/^components\/ui\//, "").replace(/\.tsx$/, ""));
const gallerySource = walk(join(web, "app", "ui-gallery"), (n) => n.endsWith(".tsx")).map(read).join("\n");
const inGallery = components.filter((c) => gallerySource.includes(`@/components/ui/${c}"`));
const notInGallery = components.filter((c) => !inGallery.includes(c));
const sections = [...gallerySource.matchAll(/aria-labelledby="(cg-[a-z0-9-]+)"/g)].map((m) => m[1]);

// Accessibility exclusions (spec 038 A11Y-008) and how close each review date is.
const exclusionsSource = read(join(repo, "tests", "Acceptance", "a11y", "exclusions.ts"));
const exclusions = [...exclusionsSource.matchAll(/rule:\s*"([^"]+)"[\s\S]*?selector:\s*"([^"]+)"[\s\S]*?review:\s*"(\d{4}-\d{2}-\d{2})"/g)].map((m) => ({
  rule: m[1],
  selector: m[2],
  review: m[3],
}));

const list = (items) => (items.length === 0 ? "None.\n" : items.map((i) => `- \`${i}\``).join("\n") + "\n");
const share = (part, whole) => (whole === 0 ? "0%" : `${Math.round((part / whole) * 100)}%`);

const report = `# Design audit ${quarter}

Generated ${date} by \`node scripts/design-audit.mjs\` (spec 036 T013). The counted part covers the code; the live
screen pass and the drift report are filled in by the reviewer as described in \`docs/design-system/audit.md\`.

## Summary

| Measure | Count | Note |
|---|---|---|
| Pages | ${pages.length} | Every \`page.tsx\` under \`app/\` |
| Pages still without the shared page header | ${headerDebt.length} | ${share(headerDebt.length, pages.length)} of pages; the baseline may only shrink (DLS-105) |
| Own tab strips and filter bars | ${ownStrips.length} | Waiting to move onto Tabs, SectionNav or FilterToolbar (DLS-106) |
| Home placements off the Widget frame | ${ownFrame.length} | Listed exceptions (DLS-089) |
| Feature classes on the Studio surface list | ${studioFeature.length} | Each should become a shared part (T037a) |
| Shared components | ${components.length} | \`components/ui\` |
| Shared components the gallery shows | ${inGallery.length} | ${share(inGallery.length, components.length)} (DLS-117) |
| Gallery sections | ${sections.length} | Each one is in the design suite |
| Accessibility exclusions | ${exclusions.length} | Each has a reason and a review date (A11Y-008) |

## Pages without the shared page header

${list(headerDebt)}
## Own tab strips and filter bars

${list(ownStrips)}
## Home placements off the Widget frame

${list(ownFrame)}
## Feature classes on the Studio surface list

${list(studioFeature)}
## Shared components the gallery does not import

These are internal parts (hooks, helpers, overlays used through another component) or a gap to close.

${list(notInGallery)}
## Accessibility exclusions

${exclusions.length === 0 ? "None.\n" : "| Rule | Selector | Review by | Status |\n|---|---|---|---|\n" + exclusions.map((e) => `| ${e.rule} | \`${e.selector}\` | ${e.review} | ${e.review < date ? "Overdue" : "Open"} |`).join("\n") + "\n"}
## Live screen pass (reviewer)

Walk the primary pages of spec 038 in each design, light and dark, Arabic and English, against the gallery and the
detail checklist (\`docs/design-system/foundations/details.md\`). Record each finding with the page, the rule it breaks
and the fix order. Write "None" when the pass finds nothing.

## Drift report (reviewer)

For each pilot tenant, open the drift report in the brand manager (spec 033 T022) and record the counts of
non-compliant asset usage, conflicting overrides and inaccessible elements, with the remediation agreed. Counts only:
no tenant names, people or content in this report.

## Actions

List what this audit changes: gate baselines to shrink, components to add to the gallery, tasks to open.
`;

if (output) {
  writeFileSync(output, report);
  console.log(`Design audit ${quarter} written to ${output}.`);
} else {
  process.stdout.write(report);
}
