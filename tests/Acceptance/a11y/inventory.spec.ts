import { readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { exclusions } from "./exclusions";
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
