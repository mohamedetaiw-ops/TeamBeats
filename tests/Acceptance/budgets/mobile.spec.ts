import type { BrowserContext, Page } from "@playwright/test";
import { expect, test } from "../support/fixtures";
import { sessionToken } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 030 section 7 on a production build: the home page loads in under 3 seconds on a mid-range phone over 4G, and
 * the app shell paints in under 1 second once the service worker keeps it. Both use the Lighthouse mobile profile
 * (a 412 by 823 phone screen, slow 4G with a 150 ms round trip, 1.6 Mbps down and 750 kbps up, and a CPU four times
 * slower) applied in the browser, and "loads" is the Largest Contentful Paint. The lighthouse package itself is not
 * used: it brings a dependency under a license that is not OSI approved (CLAUDE.md rule 6), and its installability
 * category is gone; the manifest, icons and service worker are checked by AppInstallTests and mobile.spec.
 */
const budget = { lcp: 3000, cachedPaint: 1000 };
const phone = { width: 412, height: 823 };
const slow4g = { offline: false, latency: 150, downloadThroughput: (1638.4 * 1024) / 8, uploadThroughput: (750 * 1024) / 8 };
const cpuSlowdown = 4;

test.skip(stack.webMode !== "production", "The budgets apply to a production build only (E2E_WEB_MODE=production).");

/** Slows the page down to the mobile profile from the next request on. */
async function throttle(context: BrowserContext, page: Page): Promise<void> {
  const cdp = await context.newCDPSession(page);
  await cdp.send("Network.enable");
  await cdp.send("Network.emulateNetworkConditions", slow4g);
  await cdp.send("Emulation.setCPUThrottlingRate", { rate: cpuSlowdown });
}

/** The page's own paint timings once it has loaded: first contentful paint and largest contentful paint. */
async function paints(page: Page): Promise<{ fcp: number; lcp: number }> {
  await page.waitForLoadState("load");
  return page.evaluate(
    () =>
      new Promise<{ fcp: number; lcp: number }>((resolve) => {
        let fcp = Number.POSITIVE_INFINITY;
        let lcp = Number.POSITIVE_INFINITY;
        new PerformanceObserver((list) => {
          const first = list.getEntriesByName("first-contentful-paint")[0];
          if (first) fcp = first.startTime;
        }).observe({ type: "paint", buffered: true });
        new PerformanceObserver((list) => {
          const last = list.getEntries().at(-1);
          if (last) lcp = last.startTime;
        }).observe({ type: "largest-contentful-paint", buffered: true });
        // Buffered entries arrive in a task after observe; the largest paint is final once the page has loaded.
        setTimeout(() => resolve({ fcp, lcp }), 500);
      }),
  );
}

for (const locale of ["en", "ar"] as const) {
  test.describe(`mobile performance in ${locale}`, () => {
    let tenant: TestTenant;
    let token: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`perf-${locale}`, locale, "workplace_plus");
      token = await sessionToken(tenant);
    });

    test("the home page loads within 3 seconds and the cached app shell paints within a second on a mid-range phone", async ({ browser }, testInfo) => {
      test.setTimeout(180_000);
      const home = `${hostOrigin(tenant.key)}/`;
      const signedIn = async () => {
        const context = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US", viewport: phone, isMobile: true, hasTouch: true });
        await context.addCookies([{ name: "tb_session", value: token, url: home }]);
        return context;
      };

      // Opened once first, so the measured load does not include the server's first render of the route.
      const warm = await signedIn();
      await (await warm.newPage()).goto(home);
      await warm.close();

      // A first visit: nothing cached, every file over slow 4G.
      const first = await signedIn();
      const cold = await first.newPage();
      await throttle(first, cold);
      await cold.goto(home);
      expect(cold.url(), "the measured page is the signed-in home page").toBe(home);
      const coldPaints = await paints(cold);
      await first.close();

      // A later visit: the service worker keeps the build files, so only the page itself comes over the network.
      const later = await signedIn();
      const page = await later.newPage();
      await page.goto(home);
      await page.waitForFunction(() => navigator.serviceWorker.controller !== null, undefined, { timeout: 30_000 });
      // This visit fills the shell cache, now that the service worker controls the page.
      await page.reload();
      await page.waitForLoadState("load");
      await throttle(later, page);
      await page.reload();
      const cachedPaints = await paints(page);
      await later.close();

      const measured = { cold: coldPaints, cached: cachedPaints };
      console.log(`${locale}: ${JSON.stringify(measured)}`);
      await testInfo.attach(`mobile-paints-${locale}.json`, { body: JSON.stringify(measured, null, 2), contentType: "application/json" });
      expect(coldPaints.lcp, "home Largest Contentful Paint on a first visit, mobile profile").toBeLessThan(budget.lcp);
      expect(cachedPaints.fcp, "home first paint with the app shell cached, mobile profile").toBeLessThan(budget.cachedPaint);
    });
  });
}
