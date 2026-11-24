import AxeBuilder from "@axe-core/playwright";
import { test as base, expect, type Page, type Response, type TestInfo } from "@playwright/test";
import { msg, type LaunchLocale } from "./messages";
import { hostOrigin } from "./stack";

/**
 * Accessibility gate (spec 007 US5, constitution XXIV): zero critical axe violations. Serious and lower findings are
 * attached to the report so they can be triaged without blocking the build.
 */
export async function expectAccessible(page: Page, testInfo: TestInfo, label: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"]).analyze();
  await testInfo.attach(`axe-${label}.json`, { body: JSON.stringify(results.violations, null, 2), contentType: "application/json" });
  const critical = results.violations.filter((v) => v.impact === "critical");
  expect(critical.map((v) => `${v.id}: ${v.help} (${v.nodes.length} nodes)`), `critical axe violations on ${label}`).toEqual([]);
}

/** Signs in through the development sign-in form (spec 003 US1, ADR-0004) on the tenant's own host. */
export async function signInWithDevForm(page: Page, tenantKey: string, email: string, locale: LaunchLocale): Promise<void> {
  await page.goto(`${hostOrigin(tenantKey)}/signin`);
  await page.getByLabel(msg(locale, "signin.email")).fill(email);
  const webkit = page.context().browser()?.browserType().name() === "webkit";
  const signedIn = webkit ? page.waitForResponse((r) => r.url().endsWith("/dev/signin") && r.request().method() === "POST") : null;
  await page.getByRole("button", { name: msg(locale, "signin.submit") }).click();
  if (signedIn) await keepSecureCookies(page, await signedIn);
  await page.waitForURL(`${hostOrigin(tenantKey)}/`);
}

/**
 * The session cookie is always Secure. Chromium and Firefox keep a Secure cookie on http://*.localhost, which the
 * suite uses, but WebKit on Linux drops it, so the nightly WebKit run (spec 036 T012) copies the cookies of the sign-in
 * response into the browser context without the flag. Test support only; the product cookie is unchanged.
 */
async function keepSecureCookies(page: Page, response: Response): Promise<void> {
  const url = new URL(response.url());
  const cookies = (await response.headersArray())
    .filter((h) => h.name.toLowerCase() === "set-cookie")
    .map((h) => h.value.split(";")[0])
    .map((pair) => ({ name: pair.slice(0, pair.indexOf("=")).trim(), value: pair.slice(pair.indexOf("=") + 1).trim() }))
    .filter((c) => c.name.length > 0 && c.value.length > 0);
  await page.context().addCookies(cookies.map((c) => ({ ...c, domain: url.hostname, path: "/", httpOnly: true, secure: false, sameSite: "Lax" as const })));
}

/**
 * Every test fails on a content security policy violation (spec 026 FR-025): the web app must work under its own
 * policy, with no inline script and nothing loaded from another site.
 */
export const test = base.extend<{ cspViolationsExpected: boolean; cspGuard: void }>({
  // Only a test that injects content on purpose to prove the policy blocks it sets this.
  cspViolationsExpected: [false, { option: true }],
  cspGuard: [
    async ({ page, cspViolationsExpected }, use) => {
      const violations: string[] = [];
      page.on("console", (message) => {
        if (message.type() === "error" && message.text().includes("Content Security Policy")) {
          violations.push(message.text());
        }
      });
      await use();
      if (!cspViolationsExpected) {
        expect(violations, "content security policy violations").toEqual([]);
      }
    },
    { auto: true },
  ],
});
export { expect };
