import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 008 R3 US6 in the browser (T028): a tenant administrator turns on personalization from use with opt-in, and a
 * person then finds the choice in their preferences and agrees. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`behavioral personalization in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string) => msg(locale, key);

    test.beforeAll(async () => {
      tenant = await createTenant(`bhv-${locale}`, locale, "enterprise_gov");
    });

    test("the policy is turned on and a person agrees", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/settings`);
      await expect(page.getByTestId("behavior-choice")).toHaveCount(0); // Off until the tenant turns it on.

      await page.goto(`${hostOrigin(tenant.key)}/admin/shell`);
      const policy = page.getByTestId("behavior-policy");
      await expect(policy.getByRole("heading", { name: m("shell_settings.behavior.title") })).toBeVisible();
      await policy.getByLabel(m("shell_settings.behavior.enabled")).check();
      await policy.getByLabel(m("shell_settings.behavior.consent")).selectOption("opt_in");
      await policy.getByRole("button", { name: m("shell_settings.behavior.save") }).click();
      await expect(policy.getByText(m("shell_settings.behavior.saved"))).toBeVisible();
      await expectAccessible(page, testInfo, `behavior-policy-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/settings`);
      const choice = page.getByTestId("behavior-choice");
      const use = choice.getByLabel(m("preferences.behavior.use"));
      await expect(use).not.toBeChecked();
      await use.click(); // The switch shows the answer of the API.
      await expect(use).toBeChecked();
      await page.reload();
      await expect(page.getByTestId("behavior-choice").getByLabel(m("preferences.behavior.use"))).toBeChecked();
      await expectAccessible(page, testInfo, `behavior-choice-${locale}`);
    });
  });
}
