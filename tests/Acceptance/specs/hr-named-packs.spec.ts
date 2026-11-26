import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 019 R3 US8: in Enterprise and Government the marketplace offers Oracle HCM Cloud and SAP SuccessFactors, and
 * their setup asks only for the address, sign-in and nightly time because the API holds the mapping; in Workplace Plus
 * they need another package. In English and Arabic with axe. Nothing calls an HR system.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`named HR packs in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let enterprise: TestTenant;
    let plus: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      enterprise = await createTenant(`hrnamed-${locale}`, locale, "enterprise_gov");
      plus = await createTenant(`hrnamed-plus-${locale}`, locale, "workplace_plus");
    });

    test("Add opens the Oracle HCM setup with only the address, sign-in and schedule", async ({ page }, testInfo) => {
      await signInWithDevForm(page, enterprise.key, enterprise.adminEmail, locale);
      await page.goto(`${hostOrigin(enterprise.key)}/admin/connections/marketplace`);
      const oracle = page.getByTestId("pack-hr_oracle_hcm");
      await expect(oracle).toContainText(m("connections.marketplace.inPackage"));
      await oracle.getByRole("link", { name: m("connections.marketplace.addNamed", { name: m("connections.kinds.hr_oracle_hcm") }) }).click();
      await expect(page.getByRole("heading", { name: m("connections.hr.named.hr_oracle_hcm.addTitle") })).toBeVisible();
      await expect(page.getByLabel(m("connections.hr.timeZone"))).toBeVisible();
      await expect(page.getByLabel(m("connections.hr.employeesPath"))).toHaveCount(0);
      await expectAccessible(page, testInfo, `hr-named-setup-${locale}`);
    });

    test("a package without named HR packs lists them as needing another package", async ({ page }) => {
      await signInWithDevForm(page, plus.key, plus.adminEmail, locale);
      await page.goto(`${hostOrigin(plus.key)}/admin/connections/marketplace`);
      const sf = page.getByTestId("pack-hr_sap_successfactors");
      await expect(sf).toContainText(m("connections.marketplace.notInPackage"));
      await expect(sf.getByRole("link")).toHaveCount(0);
      await expect(page.getByTestId("pack-hr_rest")).toContainText(m("connections.marketplace.inPackage"));
    });
  });
}
