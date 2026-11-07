import type { Page } from "@playwright/test";
import { expect, signInWithDevForm, test } from "../support/fixtures";
import { createPage, fillWithReferencePacks, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 032 T021, FR-058 and SC-005: every reference template, filled with both reference packs, stays within the Core Web
 * Vitals budget (LCP 2.5 s, INP 200 ms, CLS 0.1) on the two reference profiles, and the renderer sends its vitals as
 * telemetry when the page is hidden. Each page is opened once first, so the measured load does not include the
 * development server compiling the route.
 */
const templates = ["T01", "T02", "T03", "T04", "T05", "T06", "T07", "T08", "T09", "T10", "T11", "T12"];
const profiles = [
  { name: "mobile", viewport: { width: 375, height: 812 } },
  { name: "desktop", viewport: { width: 1440, height: 900 } },
] as const;
const budget = { lcp: 2500, inp: 200, cls: 0.1 };

test.skip(stack.target === "remote", "Reference component packs ship only with the development host.");

type Catalogue = { templates: { id: string; regions: { id: string; policy: { maxPlacements: number } }[] }[] };
type Measured = { lcp: number | null; cls: number; inp: number | null };

/** The test's own observers, independent of the app's collector, installed before any page script runs. */
const observers = () => {
  const w = window as unknown as { __vitals: Measured };
  w.__vitals = { lcp: null, cls: 0, inp: null };
  new PerformanceObserver((list) => {
    const last = list.getEntries().at(-1);
    if (last) w.__vitals.lcp = last.startTime;
  }).observe({ type: "largest-contentful-paint", buffered: true });
  new PerformanceObserver((list) => {
    for (const e of list.getEntries() as (PerformanceEntry & { value: number; hadRecentInput: boolean })[]) {
      if (!e.hadRecentInput) w.__vitals.cls += e.value;
    }
  }).observe({ type: "layout-shift", buffered: true });
  new PerformanceObserver((list) => {
    for (const e of list.getEntries() as (PerformanceEntry & { interactionId?: number })[]) {
      if (e.interactionId) w.__vitals.inp = Math.max(w.__vitals.inp ?? 0, e.duration);
    }
  }).observe({ type: "event", buffered: true, durationThreshold: 16 } as PerformanceObserverInit);
};

async function measure(page: Page, url: string): Promise<Measured> {
  await page.goto(url, { waitUntil: "load" });
  await expect(page.locator("[data-template]")).toBeVisible();
  await page.waitForLoadState("networkidle");
  // One interaction for INP: a click on the page body, which every template has.
  await page.locator("main").click({ position: { x: 5, y: 5 } });
  await page.waitForTimeout(0);
  return page.evaluate(() => (window as unknown as { __vitals: Measured }).__vitals);
}

test.describe("performance budgets", () => {
  test.describe.configure({ mode: "serial" });
  test.use({ locale: "en-US" });

  let tenant: TestTenant;
  const slugs = new Map<string, string>();

  test.beforeAll(async () => {
    tenant = await createTenant("perf", "en");
    const api = await tenantApi(tenant);
    const catalogue = (await (await api.get("/api/v1/experience/catalogue")).json()) as Catalogue;
    for (const template of templates) {
      const page = await createPage(api, template, `perf-${template.toLowerCase()}`);
      const max = Object.fromEntries(catalogue.templates.find((t) => t.id === template)!.regions.map((r) => [r.id, r.policy.maxPlacements]));
      await saveAndPublish(api, page, await fillWithReferencePacks(api, page, max));
      slugs.set(template, page.slug);
    }
    await api.dispose();
  });

  for (const profile of profiles) {
    test(`every reference template meets LCP, INP and CLS on the ${profile.name} profile`, async ({ page }) => {
      test.slow();
      await page.setViewportSize(profile.viewport);
      await page.addInitScript(observers);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
      const results: string[] = [];
      for (const template of templates) {
        const url = `${hostOrigin(tenant.key)}/p/${slugs.get(template)}`;
        await page.goto(url, { waitUntil: "load" });
        const m = await measure(page, url);
        results.push(`${template}: LCP ${Math.round(m.lcp ?? -1)} ms, INP ${Math.round(m.inp ?? 0)} ms, CLS ${m.cls.toFixed(3)}`);
        expect.soft(m.lcp, `${template} LCP on ${profile.name}`).not.toBeNull();
        expect.soft(m.lcp ?? Infinity, `${template} LCP on ${profile.name}`).toBeLessThanOrEqual(budget.lcp);
        expect.soft(m.inp ?? 0, `${template} INP on ${profile.name}`).toBeLessThanOrEqual(budget.inp);
        expect.soft(m.cls, `${template} CLS on ${profile.name}`).toBeLessThanOrEqual(budget.cls);
      }
      await test.info().attach(`web-vitals-${profile.name}`, { body: results.join("\n"), contentType: "text/plain" });
    });
  }

  test("the renderer sends its vitals as telemetry when the page is hidden", async ({ page }) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/p/${slugs.get("T04")}`);
    await expect(page.locator('[data-template="T04"]')).toBeVisible();
    // The collector starts once the page has hydrated.
    await page.waitForLoadState("networkidle");
    // A beacon's body is not exposed to the test, so the API's answer shows the event was well formed and accepted.
    const sent = page.waitForResponse((r) => r.url().endsWith("/api/v1/experience/telemetry") && r.request().method() === "POST");
    await page.evaluate(() => {
      Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "hidden" });
      document.dispatchEvent(new Event("visibilitychange"));
    });
    const response = await sent;
    expect(response.status()).toBe(202);
    expect(((await response.json()) as { accepted: number }).accepted).toBeGreaterThanOrEqual(1);
  });
});
