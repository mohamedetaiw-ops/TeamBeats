import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const people = ["s1", "s2", "s3", "s4", "s5"];

/**
 * Spec 021 US3 in the browser: five people search for something that is not there; the administrator opens search gaps
 * from the analytics workspace, finds the search among those with no results, and starts a knowledge article for it
 * with the search as its title. Each first misspelled the search, so the change of words shows as a refinement, and
 * all five are in Operations, so the administrator can open the searches of that unit (T021). In English and Arabic,
 * with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`search gaps in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`sgp-${locale}`, locale, "workplace_plus", people);
      const admin = await tenantApi(tenant);
      const unit = await admin.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "OPS", names: { en: "Operations", ar: "العمليات" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      for (const person of people) {
        const client = await tenantApiAs(tenant, `${person}@${tenant.key}.example`);
        const me = (await (await client.get("/api/v1/me")).json()) as { id: string };
        const member = await admin.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } });
        expect(member.ok(), await member.text()).toBe(true);
        expect((await client.get(`/api/v1/search?q=${encodeURIComponent("parkng permit")}`)).ok()).toBe(true);
        const searched = await client.get(`/api/v1/search?q=${encodeURIComponent("parking permit")}`);
        expect(searched.ok(), await searched.text()).toBe(true);
        await client.dispose();
      }
      await admin.dispose();
    });

    test("an administrator finds a search with no results and starts an article for it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.searchLink") }).click();
      await page.waitForURL(/\/analytics\/search$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.search.title") })).toBeVisible();
      const zero = page.getByTestId("search-zero");
      await expect(zero).toContainText("parking permit");
      await expect(page.getByTestId("search-refinements")).toContainText("parkng permit");
      await expectAccessible(page, testInfo, `analytics-search-${locale}`);

      // Open the searches of one unit, then go back to all of them.
      const operations = locale === "ar" ? "العمليات" : "Operations";
      await page.getByTestId("search-units").getByRole("link", { name: operations }).click();
      await page.waitForURL(/\/analytics\/search\?unit=/);
      await expect(page.getByText(m("analytics.search.unitFilter", { unit: operations }), { exact: true })).toBeVisible();
      await expect(page.getByTestId("search-zero")).toContainText("parking permit");
      await expectAccessible(page, testInfo, `analytics-search-unit-${locale}`);
      await page.getByRole("link", { name: m("analytics.search.allUnits") }).click();
      await page.waitForURL(/\/analytics\/search$/);

      await zero.getByRole("link", { name: m("analytics.search.articleFor", { query: "parking permit" }) }).click();
      await page.waitForURL(/\/kb\/new\?/);
      await expect(page.locator("input[value='parking permit']")).toHaveCount(1);
    });
  });
}
