import AxeBuilder from "@axe-core/playwright";
import { expect, type Page, type TestInfo } from "@playwright/test";
import { exclusions } from "../a11y/exclusions";
import { routes } from "../a11y/routes";
import { tenantApi } from "./pages";
import type { TestTenant } from "./platform";

/** The WCAG 2.0, 2.1 and 2.2 level A and AA rules (spec 038 A11Y-006). */
export const wcagTags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa"];

/** Fonts and images loaded, so contrast and size are measured on what a person sees. */
export async function settle(page: Page): Promise<void> {
  await page.waitForLoadState("networkidle", { timeout: 15_000 }).catch(() => undefined);
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all(
      [...document.images]
        .filter((img) => !img.complete)
        .map(
          (img) =>
            new Promise((resolve) => {
              img.addEventListener("load", resolve, { once: true });
              img.addEventListener("error", resolve, { once: true });
              setTimeout(resolve, 3000);
            }),
        ),
    );
  });
}

/**
 * The conformance gate (spec 038 A11Y-006): zero critical and zero serious axe violations, apart from the listed
 * exclusions. Every finding, blocking or not, is attached to the report.
 */
export async function expectConformant(page: Page, testInfo: TestInfo, label: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze();
  if (results.violations.length > 0) {
    await testInfo.attach(`axe-${label}.json`, { body: JSON.stringify(results.violations, null, 2), contentType: "application/json" });
  }
  const blocking = results.violations
    .filter((v) => v.impact === "critical" || v.impact === "serious")
    .map((v) => ({
      ...v,
      // An exclusion removes one rule on the elements its selector names, never the rule everywhere.
      nodes: v.nodes.filter((n) => !exclusions.some((e) => e.rule === v.id && n.target.join(" ").includes(e.selector))),
    }))
    .filter((v) => v.nodes.length > 0);
  expect
    .soft(
      // The first node's own message (for contrast: the colours and the ratio) makes a CI failure readable without the report.
      blocking.map((v) => `${v.impact} ${v.id}: ${v.help} (${v.nodes.slice(0, 5).map((n) => n.target.join(" ")).join(", ")}) ${v.nodes[0]?.any[0]?.message ?? ""}`.trim()),
      `serious or critical axe violations on ${label}`,
    )
    .toEqual([]);
}

/** Sets the tenant's design through its brand token (spec 036). */
export async function chooseDesign(tenant: TestTenant, design: string): Promise<void> {
  const api = await tenantApi(tenant);
  try {
    const brands = (await (await api.get("/api/v1/experience/brands")).json()) as { id: string; name: string }[];
    const saved = await api.put(`/api/v1/experience/brands/${brands[0]!.id}`, {
      data: { name: brands[0]!.name, tokens: { "design.preset": { $value: design } } },
    });
    expect(saved.ok(), await saved.text()).toBe(true);
  } finally {
    await api.dispose();
  }
}

/** Sets the administrator's colour mode through their preference profile (spec 008). */
export async function chooseMode(tenant: TestTenant, mode: string): Promise<void> {
  const api = await tenantApi(tenant);
  try {
    const saved = await api.put("/api/v1/me/preference-profile", { data: { values: { mode } } });
    expect(saved.ok(), await saved.text()).toBe(true);
  } finally {
    await api.dispose();
  }
}

const designs = ["beats", "atlas", "pulse", "studio"] as const;
const widths = [
  { name: "desktop", width: 1280, height: 900 },
  { name: "phone", width: 390, height: 844 },
] as const;

/** The detail routes that need workers, a connected system, HR setup or a device: their feature suites check them. */
export const suiteCheckedRoutes = routes.filter((r) => r.tier === "detail" && r.reason && !r.reason.includes("checked here")).map((r) => r.path);

/**
 * A detail route checked on the record its feature suite built (spec 038 T022). The page is open on that record. The
 * route gets the design, colour mode and width the matrix rotation gives it, the same way `a11y/details.spec.ts` turns
 * the routes it seeds itself, and the gate runs. The page then goes back to the default design, mode and width, so the
 * rest of the suite carries on as before. A signed-out page has no tenant design, so only its mode and width turn.
 */
/** Puts the given tokens on the tenant's first brand and returns the tokens it had, so they can be put back. */
async function putBrandTokens(tenant: TestTenant, change: (tokens: Record<string, unknown>) => Record<string, unknown>): Promise<Record<string, unknown>> {
  const api = await tenantApi(tenant);
  try {
    const brands = (await (await api.get("/api/v1/experience/brands")).json()) as { id: string }[];
    const brand = (await (await api.get(`/api/v1/experience/brands/${brands[0]!.id}`)).json()) as { name: string; tokens: Record<string, unknown> };
    const saved = await api.put(`/api/v1/experience/brands/${brands[0]!.id}`, { data: { name: brand.name, tokens: change(brand.tokens) } });
    expect(saved.ok(), await saved.text()).toBe(true);
    return brand.tokens;
  } finally {
    await api.dispose();
  }
}

export async function expectDetailConformant(page: Page, testInfo: TestInfo, tenant: TestTenant, route: string): Promise<void> {
  const index = suiteCheckedRoutes.indexOf(route);
  expect(index, `${route} is a detail route with a reason in a11y/routes.ts`).toBeGreaterThanOrEqual(0);
  const html = page.locator("html");
  const lang = (await html.getAttribute("lang")) ?? "en";
  const signedIn = (await html.getAttribute("data-design")) !== null;
  // The other language starts half way round, so each route is seen in two designs.
  const design = designs[(index + (lang.startsWith("ar") ? 2 : 0)) % designs.length]!;
  const mode = Math.floor(index / designs.length) % 2 === 0 ? "light" : "dark";
  const width = widths[Math.floor(index / (designs.length * 2)) % widths.length]!;
  const viewport = page.viewportSize();
  const before = signedIn ? await putBrandTokens(tenant, (tokens) => ({ ...tokens, "design.preset": { $value: design } })) : null;
  await page.emulateMedia({ colorScheme: mode });
  await page.setViewportSize(width);
  try {
    await page.reload();
    if (signedIn) await expect(html).toHaveAttribute("data-design", design);
    await settle(page);
    await expectConformant(page, testInfo, `${signedIn ? design : "public"}-${mode}-${lang}-${width.name}${route.replace(/[/[\]]+/g, "-")}`);
  } finally {
    if (before) await putBrandTokens(tenant, () => before);
    await page.emulateMedia({ colorScheme: null });
    if (viewport) await page.setViewportSize(viewport);
    await page.reload();
    await page.waitForLoadState("networkidle", { timeout: 15_000 }).catch(() => undefined);
  }
}
