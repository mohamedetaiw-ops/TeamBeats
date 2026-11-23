import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 US5 (FR-025): before moving an organization the administrator sees what changes for it, here the brand it
 * takes from its new parent, and then confirms the move. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`move impact in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const plant = { en: "Gulf Plant", ar: "مصنع الخليج" };
    const africa = { en: "Acme Africa", ar: "أكمي أفريقيا" };

    test.beforeAll(async () => {
      tenant = await createTenant(`mvi-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = async (parentId: string | null, code: string, names: Record<string, string>) => {
        const created = await api.post("/api/v1/org/units", { data: { parentId, typeKey: "department", code, names } });
        expect(created.ok(), await created.text()).toBe(true);
        const body = (await created.json()) as { id?: string; unit?: { id: string } };
        return body.unit?.id ?? body.id!;
      };
      const gulf = await unit(null, "GLF", { en: "Acme Gulf", ar: "أكمي الخليج" });
      await unit(gulf, "PLT", plant);
      const africaId = await unit(null, "AFR", africa);
      const brand = await api.post("/api/v1/experience/brands", { data: { name: "Africa", tokens: { "color.accent.base": { $value: "#b45309" } } } });
      expect(brand.ok(), await brand.text()).toBe(true);
      const brandId = ((await brand.json()) as { id: string }).id;
      const assigned = await api.put(`/api/v1/experience/brand-assignments/${africaId}`, { data: { brandId } });
      expect(assigned.ok(), await assigned.text()).toBe(true);
      await api.dispose();
    });

    test("an administrator sees what a move changes before making it", async ({ page }, testInfo) => {
      const o = (key: string, values: Record<string, string> = {}) => msg(locale, `admin.org.${key}`, values);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/organization`);
      await expect(async () => {
        await page.getByRole("treeitem", { name: plant[locale] }).first().click();
        await page.getByRole("button", { name: o("actions.move"), exact: true }).click({ timeout: 1_000 });
        await expect(page.getByRole("dialog")).toBeVisible({ timeout: 1_000 });
      }).toPass();
      const dialog = page.getByRole("dialog");
      await dialog.getByLabel(o("form.newParent"), { exact: true }).selectOption({ label: africa[locale] });
      const impact = dialog.getByTestId("move-impact");
      await expect(impact).toContainText(o("form.impact.title", { name: plant[locale] }));
      await expect(impact).toContainText("Africa");
      await expectAccessible(page, testInfo, "move-impact");
      await dialog.getByRole("button", { name: o("form.moveSubmit"), exact: true }).click();
      await expect(page.getByRole("dialog")).toBeHidden();
    });
  });
}
