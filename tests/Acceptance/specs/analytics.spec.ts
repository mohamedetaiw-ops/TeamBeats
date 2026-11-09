import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 T007 and T008: the tenant administrator opens adoption basics from the admin navigation, sees the table
 * views, and opens the frontend error list, filtered by release, with the component version and correlation ID and
 * never a message. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`analytics admin in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`anl-${locale}`, locale, "workplace");
      const api = await tenantApi(tenant);
      const sent = await api.post("/api/v1/analytics/events", {
        data: {
          events: [
            { name: "ui.error", fields: { kind: "render", release: "rel-1", component: "toolbox.weather@1", area: "pages", correlationId: "corr-1" } },
            { name: "ui.error", fields: { kind: "chunk", release: "rel-2", area: "content" } },
          ],
        },
      });
      expect(sent.status(), await sent.text()).toBe(202);
      await api.dispose();
    });

    test("adoption basics and the frontend error list", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("link", { name: msg(locale, "admin.nav.analytics") }).click();
      await page.waitForURL(/\/admin\/analytics$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "analytics.adoption.title"));
      await expect(page.getByRole("heading", { level: 2, name: msg(locale, "analytics.adoption.templates") })).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-adoption-${locale}`);

      await page.getByRole("link", { name: msg(locale, "analytics.adoption.errorsLink") }).click();
      await page.waitForURL(/\/admin\/analytics\/errors$/);
      const groups = page.getByTestId("frontend-error-groups");
      await expect(groups.locator("tbody tr")).toHaveCount(2);
      await expectAccessible(page, testInfo, `analytics-errors-${locale}`);

      await page.getByLabel(msg(locale, "analytics.errors.release")).selectOption("rel-1");
      await page.getByRole("button", { name: msg(locale, "analytics.errors.filter") }).click();
      await page.waitForURL(/release=rel-1/);
      await expect(groups.locator("tbody tr")).toHaveCount(1);
      await expect(groups).toContainText("toolbox.weather@1");
      await expect(groups).toContainText("corr-1");
      await expect(groups).toContainText(msg(locale, "analytics.errors.kind.render"));
    });
  });
}
