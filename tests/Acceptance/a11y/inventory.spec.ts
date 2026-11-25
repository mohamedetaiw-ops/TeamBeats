import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { exclusions, unseededRoutes } from "./exclusions";
import { suiteCheckedRoutes } from "../support/a11y";
import { routes } from "./routes";

/** Spec 038 A11Y-004 and A11Y-008: the route inventory is complete and every exclusion is justified and current. */
const app = fileURLToPath(new URL("../../../src/Web/app", import.meta.url));

function pageRoutes(dir: string, prefix = ""): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) found.push(...pageRoutes(full, `${prefix}/${entry}`));
    else if (entry === "page.tsx") found.push(prefix === "" ? "/" : prefix);
  }
  return found;
}

test("every page route of the web app is in the accessibility inventory", () => {
  const listed = new Set(routes.map((r) => r.path));
  const missing = pageRoutes(app).filter((path) => !listed.has(path));
  expect(missing, "routes missing from tests/Acceptance/a11y/routes.ts").toEqual([]);
  const gone = routes.filter((r) => !pageRoutes(app).includes(r.path)).map((r) => r.path);
  expect(gone, "inventory routes that no longer exist").toEqual([]);
  expect(routes.filter((r) => r.tier === "skip" && !r.reason).map((r) => r.path), "skipped routes without a reason").toEqual([]);
});

test("every exclusion names its reason and is not past its review date", () => {
  const today = new Date().toISOString().slice(0, 10);
  for (const e of exclusions) {
    expect(e.reason.trim().length, `reason for ${e.rule} on ${e.selector}`).toBeGreaterThan(10);
    expect(e.selector.trim(), `selector for ${e.rule}`).not.toBe("");
    expect(e.review >= today, `review date ${e.review} for ${e.rule} on ${e.selector}`).toBe(true);
  }
});

const specs = fileURLToPath(new URL("../specs", import.meta.url));

test("every detail route its feature suite opens is checked there in its matrix design, mode and width", () => {
  const sources = readdirSync(specs)
    .filter((f) => f.endsWith(".ts"))
    .map((f) => readFileSync(join(specs, f), "utf8"))
    .join("\n");
  const unseeded = new Set(unseededRoutes.map((u) => u.route));
  const unchecked = suiteCheckedRoutes.filter((route) => !unseeded.has(route) && !sources.includes(`, testInfo, tenant, "${route}")`));
  expect(unchecked, "detail routes with a reason in routes.ts and no expectDetailConformant call in their feature suite").toEqual([]);
  const today = new Date().toISOString().slice(0, 10);
  for (const u of unseededRoutes) {
    expect(suiteCheckedRoutes, `${u.route} is a detail route with a reason in routes.ts`).toContain(u.route);
    expect(u.reason.trim().length, `reason for ${u.route}`).toBeGreaterThan(10);
    expect(u.review >= today, `review date ${u.review} for ${u.route}`).toBe(true);
  }
});
