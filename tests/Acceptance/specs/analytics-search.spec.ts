import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const people = ["s1", "s2", "s3", "s4", "s5"];

/**
 * Spec 021 US3 in the browser: five people search for something that is not there; the administrator opens search gaps
 * from the analytics workspace, finds the search among those with no results, and starts a knowledge article for it
 * with the search as its title. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`search gaps in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`sgp-${locale}`, locale, "workplace_plus", people);
      for (const person of people) {
        const client = await tenantApiAs(tenant, `${person}@${tenant.key}.example`);
        const searched = await client.get(`/api/v1/search?q=${encodeURIComponent("parking permit")}`);
        expect(searched.ok(), await searched.text()).toBe(true);
        await client.dispose();
      }
    });

    test("an administrator finds a search with no results and starts an article for it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.searchLink") }).click();
      await page.waitForURL(/\/analytics\/search$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.search.title") })).toBeVisible();
      const zero = page.getByTestId("search-zero");
      await expect(zero).toContainText("parking permit");
      await expectAccessible(page, testInfo, `analytics-search-${locale}`);

      await zero.getByRole("link", { name: m("analytics.search.articleFor", { query: "parking permit" }) }).click();
      await page.waitForURL(/\/kb\/new\?/);
      await expect(page.locator("input[value='parking permit']")).toHaveCount(1);
    });
  });
}
