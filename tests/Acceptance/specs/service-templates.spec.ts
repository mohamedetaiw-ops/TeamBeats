import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 016 R2 T025 in the browser (section 12): a tenant administrator switches on a starter template and lands on
 * its draft in the route designer; the template list then links to the service. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`service templates in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const name = locale === "ar" ? "بطاقات العمل" : "Business cards";

    test.beforeAll(async () => {
      tenant = await createTenant(`tpl-${locale}`, locale, "workplace_plus", []);
    });

    test("an administrator switches on a template and edits its draft", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/services`);
      const templates = page.getByRole("region", { name: m("services.templates.title") });
      await expect(templates.getByTestId("service-template")).toHaveCount(12);
      await expectAccessible(page, testInfo, `services-templates-${locale}`);

      await templates.getByRole("button", { name: m("services.templates.switchOnNamed", { name }) }).click();
      await page.waitForURL(/\/admin\/services\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
      await expect(page.getByTestId("route-designer")).toBeVisible();
      await expect(page.getByTestId("route-outline").getByRole("listitem")).toHaveCount(3);

      await page.goto(`${hostOrigin(tenant.key)}/admin/services`);
      await expect(templates.getByRole("link", { name: m("services.templates.open", { name }) })).toBeVisible();
    });
  });
}
