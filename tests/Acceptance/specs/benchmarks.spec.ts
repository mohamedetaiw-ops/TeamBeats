import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 R3 benchmarks in the browser (T026): a tenant administrator sees that the organization does not take part,
 * turns taking part on and sees the last complete week with the organization count and hidden quartiles, then turns it
 * off again. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`benchmarks in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`bm-${locale}`, locale, "enterprise_gov");
    });

    test("an administrator turns taking part on and off", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/analytics/benchmarks`);
      await expect(page.getByRole("heading", { name: m("analytics.benchmarks.title"), level: 1 })).toBeVisible();
      await expect(page.getByText(m("analytics.benchmarks.off"))).toBeVisible();

      const take = page.getByLabel(m("analytics.benchmarks.switch"));
      await take.click();
      await expect(page.getByTestId("benchmark-figures")).toBeVisible();
      const row = page.getByTestId("benchmark-figures").getByRole("row", { name: new RegExp(m("analytics.benchmarks.metric.weekly_active_share")) });
      await expect(row.getByText(m("analytics.benchmarks.hidden")).first()).toBeVisible();
      await expectAccessible(page, testInfo, `benchmarks-${locale}`);

      await page.getByLabel(m("analytics.benchmarks.switch")).click();
      await expect(page.getByText(m("analytics.benchmarks.off"))).toBeVisible();
    });
  });
}
