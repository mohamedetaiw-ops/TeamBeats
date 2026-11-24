import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 FR-002 and FR-019: an administrator gives an organization its own accent within its brand and locks the
 * corners for everything below it; the organization below shows the lock as coming from its parent and offers no way to
 * set that value. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`organization themes in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let gulf: string;
    let plant: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`ogt-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = async (parentId: string | null, code: string, names: Record<string, string>) => {
        const created = await api.post("/api/v1/org/units", { data: { parentId, typeKey: "department", code, names } });
        expect(created.ok(), await created.text()).toBe(true);
        const body = (await created.json()) as { id?: string; unit?: { id: string } };
        return body.unit?.id ?? body.id!;
      };
      gulf = await unit(null, "GLF", { en: "Acme Gulf", ar: "أكمي الخليج" });
      plant = await unit(gulf, "PLT", { en: "Gulf Plant", ar: "مصنع الخليج" });
      // The workspace brand is made on first use.
      expect((await api.get("/api/v1/experience/brands")).ok()).toBe(true);
      await api.dispose();
    });

    test("an administrator sets an organization's own value and lock", async ({ page }, testInfo) => {
      const o = (key: string, values: Record<string, string> = {}) => msg(locale, `brand.organizations.${key}`, values);
      const accent = msg(locale, "brand.tokenNames.color.accent.base");
      const corners = msg(locale, "brand.tokenNames.radius.md");
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding/organizations?scope=${gulf}`);
      await expect(page.getByRole("heading", { name: o("locksTitle"), exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "organization-themes");

      // Gulf keeps the workspace brand and sets its own accent.
      await expect(async () => {
        await page.getByLabel(o("setting"), { exact: true }).first().selectOption({ label: accent });
        await page.getByLabel(o("value"), { exact: true }).fill("#b45309");
        await expect(page.getByRole("button", { name: o("add"), exact: true })).toBeEnabled({ timeout: 1_000 });
      }).toPass();
      await page.getByRole("button", { name: o("add"), exact: true }).click();
      await page.getByRole("button", { name: o("saveTheme"), exact: true }).click();
      await expect(page.getByText(o("themeSaved", { scope: locale === "ar" ? "أكمي الخليج" : "Acme Gulf" }), { exact: true })).toBeVisible();
      await expect(page.getByRole("table", { name: o("overridesTitle") })).toContainText("#b45309");

      // Gulf locks the medium corners for everything below it.
      await page.getByRole("combobox", { name: o("setting"), exact: true }).last().selectOption({ label: corners });
      await page.getByRole("button", { name: o("lock"), exact: true }).click();
      await expect(page.getByText(o("locked", { setting: corners }), { exact: true })).toBeVisible();

      // The plant sees the lock as its parent's and cannot set that value.
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding/organizations?scope=${plant}`);
      const locks = page.getByRole("table", { name: o("locksTitle") });
      await expect(locks.getByRole("row").filter({ hasText: corners })).toContainText(o("inherited"));
      await expect(locks.getByRole("button", { name: o("unlockFor", { setting: corners }), exact: true })).toHaveCount(0);
      const options = await page.getByRole("combobox", { name: o("setting"), exact: true }).first().locator("option").allTextContents();
      expect(options).not.toContain(corners);
      await expectAccessible(page, testInfo, "organization-themes-child");
    });
  });
}
