import AxeBuilder from "@axe-core/playwright";
import { test as base, expect, type Page, type TestInfo } from "@playwright/test";
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
  await page.getByRole("button", { name: msg(locale, "signin.submit") }).click();
  await page.waitForURL(`${hostOrigin(tenantKey)}/`);
}

export const test = base;
export { expect };
