import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 R2 T019 (US4 scenario 3, FR-020): from the organization section an administrator delegates the brand and
 * pages of one organization to a person, sees the delegation listed and takes it back. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`delegates in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let personId: string;
    const gulf = { en: "Acme Gulf", ar: "أكمي الخليج" };

    test.beforeAll(async () => {
      tenant = await createTenant(`dlg-${locale}`, locale, "workplace_plus", ["helper"]);
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "GLF", names: gulf } });
      expect(created.ok(), await created.text()).toBe(true);
      // The person signs in once, so their account is there to delegate to.
      const helper = await tenantApiAs(tenant, `helper@${tenant.key}.example`);
      personId = ((await (await helper.get("/api/v1/me")).json()) as { id: string }).id;
      await helper.dispose();
      await api.dispose();
    });

    test("an administrator delegates an organization's brand and pages and takes them back", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/organization`);
      await page.getByRole("link", { name: msg(locale, "admin.org.delegates.open"), exact: true }).click();
      await expect(page).toHaveURL(/\/admin\/organization\/delegates$/);
      await expect(page.getByText(msg(locale, "admin.org.delegates.emptyTitle"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "delegates-empty");

      // The card draws its own box over the native one, so a person clicks the card's label.
      const areas = page.getByRole("group", { name: msg(locale, "admin.org.delegates.areas"), exact: true });
      const save = page.getByRole("button", { name: msg(locale, "admin.org.delegates.save"), exact: true });
      // The choices are made again until the form has hydrated and keeps them.
      await expect(async () => {
        await page.getByLabel(msg(locale, "admin.org.delegates.organization"), { exact: true }).selectOption({ label: gulf[locale] });
        await page.getByLabel(msg(locale, "admin.org.delegates.person"), { exact: true }).selectOption(personId);
        await expect(save).toBeEnabled({ timeout: 1_000 });
      }).toPass();
      await areas.getByText(msg(locale, "admin.org.delegates.area.brand"), { exact: true }).click();
      await areas.getByText(msg(locale, "admin.org.delegates.area.pages"), { exact: true }).click();
      await save.click();
      const table = page.getByRole("table");
      await expect(table.getByRole("row")).toHaveCount(2);
      await expect(table).toContainText(gulf[locale]);
      await expect(table).toContainText(msg(locale, "admin.org.delegates.area.brand"));
      await expectAccessible(page, testInfo, "delegates-listed");

      // Clearing every area takes the delegation back.
      await areas.getByText(msg(locale, "admin.org.delegates.area.brand"), { exact: true }).click();
      await areas.getByText(msg(locale, "admin.org.delegates.area.pages"), { exact: true }).click();
      await save.click();
      await expect(page.getByText(msg(locale, "admin.org.delegates.emptyTitle"), { exact: true })).toBeVisible();
    });
  });
}
