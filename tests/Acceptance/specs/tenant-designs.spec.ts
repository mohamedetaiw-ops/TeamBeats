import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 036 R3 (DLS-120 to DLS-122, T058): from the brand manager an administrator opens Designs, makes a design from
 * Atlas with its own accent and sends it for review; a second administrator approves it and applies it to the brand's
 * draft. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`tenant designs in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let brandName: string;
    const reviewer = () => `rana@${tenant.key}.example`;

    test.beforeAll(async () => {
      tenant = await createTenant(`tds-${locale}`, locale, "enterprise_gov", ["rana"]);
      const api = await tenantApi(tenant);
      const brands = await api.get("/api/v1/experience/brands");
      expect(brands.ok()).toBe(true);
      brandName = ((await brands.json()) as { name: string }[])[0]!.name;
      await api.dispose();
      await (await tenantApiAs(tenant, reviewer())).dispose();
    });

    test("an administrator makes a design that another approves and applies to the brand draft", async ({ page }, testInfo) => {
      const d = (key: string, values: Record<string, string> = {}) => msg(locale, `brand.designs.${key}`, values);
      const designName = locale === "ar" ? "الميناء" : "Harbour";
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding`);
      await page.getByRole("link", { name: d("open"), exact: true }).click();
      await expect(page).toHaveURL(/\/admin\/branding\/designs/);
      await expect(page.getByText(d("emptyTitle"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "designs-empty");

      const save = page.getByRole("button", { name: d("save"), exact: true });
      await expect(async () => {
        await page.getByLabel(d("key"), { exact: true }).fill("harbour");
        await expect(save).toBeEnabled({ timeout: 1_000 });
      }).toPass();
      await page.getByLabel(d("base"), { exact: true }).selectOption("atlas");
      await page.getByLabel(d("nameEn"), { exact: true }).fill("Harbour");
      await page.getByLabel(d("nameAr"), { exact: true }).fill("الميناء");
      await page.getByLabel(d("token"), { exact: true }).selectOption("color.accent.base");
      await page.getByLabel(d("value"), { exact: true }).fill("#0b5c8a");
      await page.getByLabel(d("darkValue"), { exact: true }).fill("#7cc4ef");
      await save.click();
      await expect(page.getByText(d("saved", { name: designName }), { exact: true })).toBeVisible();
      const table = page.getByRole("table").filter({ hasText: "harbour" });
      await expect(table).toContainText(d("states.draft"));
      await table.getByRole("button", { name: d("proposeFor", { name: designName }), exact: true }).click();
      await expect(table).toContainText(d("states.proposed"));
      // The person who sent it is refused when approving it.
      await table.getByRole("button", { name: d("approveFor", { name: designName }), exact: true }).click();
      await expect(page.getByText(msg(locale, "admin.common.notDone"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "designs-proposed");

      // Another administrator approves it and applies it to the brand's draft.
      await page.context().clearCookies();
      await signInWithDevForm(page, tenant.key, reviewer(), locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding/designs`);
      const again = page.getByRole("table").filter({ hasText: "harbour" });
      await again.getByRole("button", { name: d("approveFor", { name: designName }), exact: true }).click();
      await expect(again).toContainText(d("states.approved"));
      await again.getByRole("button", { name: d("applyFor", { name: designName }), exact: true }).click();
      await expect(page.getByText(d("applied", { name: designName, brand: brandName }), { exact: true })).toBeVisible();
      const draft = await page.evaluate(async () => {
        const brands = (await (await fetch("/api/v1/experience/brands", { cache: "no-store" })).json()) as { id: string }[];
        return (await (await fetch(`/api/v1/experience/brands/${brands[0]!.id}/draft`, { cache: "no-store" })).json()) as { tokens: Record<string, { $value: string }> };
      });
      expect(draft.tokens["design.preset"]?.$value).toBe("atlas");
      expect(draft.tokens["color.accent.base"]?.$value).toBe("#0b5c8a");
      await expectAccessible(page, testInfo, "designs-approved");
    });
  });
}
