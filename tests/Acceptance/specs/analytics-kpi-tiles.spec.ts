import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 R3 (US6 scenario 2): the administrator adds a KPI tile for location dashboards with its metric, names in
 * both languages and no extra permission, saves it and finds it again after a reload; in a package without KPI tiles
 * the page says they are not available. In English and Arabic with axe. Values arrive through the spec 017 KPI feed;
 * the integration suite covers them on the dashboard. All names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`analytics KPI tiles in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let enterprise: TestTenant;
    let plus: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      enterprise = await createTenant(`kpi-${locale}`, locale, "enterprise_gov");
      plus = await createTenant(`kpi-plus-${locale}`, locale, "workplace_plus");
    });

    test("an administrator adds a KPI tile and finds it after a reload", async ({ page }, testInfo) => {
      await signInWithDevForm(page, enterprise.key, enterprise.adminEmail, locale);
      await page.goto(`${hostOrigin(enterprise.key)}/admin/analytics`);
      await page.getByRole("link", { name: m("analytics.adoption.kpiTilesLink") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("analytics.kpiTiles.title"));
      await expect(page.getByText(m("analytics.kpiTiles.none"))).toBeVisible();
      await expectAccessible(page, testInfo, `kpi-tiles-empty-${locale}`);

      await page.getByRole("button", { name: m("analytics.kpiTiles.add") }).click();
      await page.getByLabel(m("analytics.kpiTiles.metric")).fill("monthly_sales");
      await page.getByLabel(m("analytics.kpiTiles.nameEn")).fill("Monthly sales");
      await page.getByLabel(m("analytics.kpiTiles.nameAr")).fill("المبيعات الشهرية");
      await page.getByRole("button", { name: m("analytics.kpiTiles.save") }).click();
      await expect(page.getByText(m("analytics.kpiTiles.saved"))).toBeVisible();

      await page.reload();
      await expect(page.getByLabel(m("analytics.kpiTiles.metric"))).toHaveValue("monthly_sales");
      await expectAccessible(page, testInfo, `kpi-tiles-${locale}`);
    });

    test("a package without KPI tiles says they are not available", async ({ page }) => {
      await signInWithDevForm(page, plus.key, plus.adminEmail, locale);
      await page.goto(`${hostOrigin(plus.key)}/admin/analytics/kpi-tiles`);
      await expect(page.getByText(m("analytics.kpiTiles.denied"))).toBeVisible();
    });
  });
}
