import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 T011 in the browser: the analytics workspace, the campaigns list with two picked for comparison, one
 * campaign's dashboard, and effort estimates set in administration then shown as return on effort. In English and
 * Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`campaign analytics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`can-${locale}`, locale, "workplace_plus");
    });

    test("a publisher follows and compares campaigns, and an administrator sets estimates", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const names = locale === "ar" ? ["حملة المزايا", "أسبوع العافية"] : ["Benefits campaign", "Wellness week"];
      const ids: string[] = [];
      for (const name of names) {
        const created = await api.post("/api/v1/content/campaigns", { data: { name, siteId: null } });
        expect(created.status()).toBe(201);
        ids.push(((await created.json()) as { id: string }).id);
      }

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);

      await page.goto(`${origin}/analytics`);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.workspace.title") })).toBeVisible();
      await expect(page.getByTestId("analytics-workspace")).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-workspace-${locale}`);

      await page.getByRole("link", { name: m("analytics.workspace.campaignsLink") }).click();
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.campaigns.title") })).toBeVisible();
      await expect(page.getByRole("link", { name: names[0] })).toBeVisible();
      await page.getByRole("checkbox", { name: names[0] }).check();
      await page.getByRole("checkbox", { name: names[1] }).check();
      await expectAccessible(page, testInfo, `analytics-campaigns-${locale}`);
      await page.getByTestId("campaigns-compare").click();
      await page.waitForURL(/\/analytics\/campaigns\/compare\?first=/);
      await expect(page.getByRole("heading", { level: 2, name: names[0] })).toBeVisible();
      await expect(page.getByRole("heading", { level: 2, name: names[1] })).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-campaigns-compare-${locale}`);

      // Before any estimates, the dashboard says where to set them.
      await page.goto(`${origin}/analytics/campaigns/${ids[0]}`);
      await expect(page.getByRole("heading", { level: 1, name: names[0] })).toBeVisible();
      await expect(page.getByTestId("campaign-effort")).toContainText(m("analytics.campaigns.effort.missing"));

      await page.goto(`${origin}/admin/analytics/estimates`);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.estimates.title") })).toBeVisible();
      await page.getByLabel(m("analytics.estimates.hours")).fill("2");
      await page.getByLabel(m("analytics.estimates.rate")).fill("100");
      await page.getByLabel(m("analytics.estimates.currency")).fill("SAR");
      await page.getByRole("button", { name: m("analytics.estimates.save") }).click();
      await expect(page.getByText(m("analytics.estimates.saved"))).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-estimates-${locale}`);

      await page.goto(`${origin}/analytics/campaigns/${ids[0]}`);
      await expect(page.getByTestId("campaign-effort")).not.toContainText(m("analytics.campaigns.effort.missing"));
      await expect(page.getByTestId("campaign-effort")).toContainText(m("analytics.campaigns.effort.total"));
      await expectAccessible(page, testInfo, `analytics-campaign-${locale}`);
    });
  });
}
