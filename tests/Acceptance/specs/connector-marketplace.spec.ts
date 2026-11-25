import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 017 R3 T023: the connector marketplace lists every connector with what the package includes, and Add opens the
 * wizard on the connector's setup. In Enterprise and Government the Oracle Fusion and SAP finance packs are included and
 * their setup asks for the ERP values; in Workplace Plus they need another package and offer no Add. In English and
 * Arabic with axe. Nothing calls an ERP; the API decides every state.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`connector marketplace in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let enterprise: TestTenant;
    let plus: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      enterprise = await createTenant(`market-${locale}`, locale, "enterprise_gov");
      plus = await createTenant(`market-plus-${locale}`, locale, "workplace_plus");
    });

    test("the marketplace shows the ERP packs as included and Add opens their setup", async ({ page }, testInfo) => {
      await signInWithDevForm(page, enterprise.key, enterprise.adminEmail, locale);
      await page.goto(`${hostOrigin(enterprise.key)}/admin/connections`);
      await page.getByRole("link", { name: m("connections.marketplace.open") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("connections.marketplace.title"));
      const sap = page.getByTestId("pack-finance_sap_s4");
      await expect(sap).toContainText(m("connections.kinds.finance_sap_s4"));
      await expect(sap).toContainText(m("connections.marketplace.inPackage"));
      await expectAccessible(page, testInfo, `connector-marketplace-${locale}`);

      await page.getByTestId("pack-finance_oracle_fusion").getByRole("link", { name: m("connections.marketplace.addNamed", { name: m("connections.kinds.finance_oracle_fusion") }) }).click();
      await expect(page.locator('[aria-current="step"]')).toContainText(m("connections.wizard.steps.setup"));
      await expect(page.getByRole("heading", { name: m("connections.erpFinance.finance_oracle_fusion.addTitle") })).toBeVisible();
      await expect(page.getByLabel(m("connections.erpFinance.finance_oracle_fusion.businessUnit"))).toBeVisible();
      await expect(page.getByLabel(m("connections.erpFinance.defaultAccount"))).toBeVisible();
      await page.getByRole("button", { name: m("connections.erpFinance.addAccount") }).click();
      await expect(page.getByLabel(m("connections.erpFinance.category"))).toBeVisible();
      await expectAccessible(page, testInfo, `erp-finance-setup-${locale}`);
    });

    test("a package without the ERP packs lists them as needing another package", async ({ page }) => {
      await signInWithDevForm(page, plus.key, plus.adminEmail, locale);
      await page.goto(`${hostOrigin(plus.key)}/admin/connections/marketplace`);
      const oracle = page.getByTestId("pack-finance_oracle_fusion");
      await expect(oracle).toContainText(m("connections.marketplace.notInPackage"));
      await expect(oracle.getByRole("link")).toHaveCount(0);
      await expect(page.getByTestId("pack-custom_rest")).toContainText(m("connections.marketplace.inPackage"));
    });
  });
}
