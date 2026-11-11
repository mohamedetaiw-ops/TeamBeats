import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 019 T005 and T008: the HR integration page. The administrator opens it from the admin area, sees that no HR
 * connection exists yet and no records, org codes or lifecycle changes are waiting, changes the matching rule, threshold,
 * joiner lead time and rehire setting, and finds both HR packs in the
 * Connections catalogue with their add forms, in English and Arabic with axe. Nothing calls an HR system: syncing,
 * matching and held runs are covered by the API tests. The API decides every rule.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`HR integration in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`hr-${locale}`, locale, "workplace_plus");
    });

    test("an administrator sets how HR records match people and finds the HR connection forms", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("link", { name: msg(locale, "admin.nav.hr_integration") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "hr_integration.title"));
      await expect(page.getByText(msg(locale, "hr_integration.status.noConnection"))).toBeVisible();
      await expect(page.getByText(msg(locale, "hr_integration.records.empty"))).toBeVisible();
      await expect(page.getByText(msg(locale, "hr_integration.lifecycle.codes.empty"))).toBeVisible();
      await expect(page.getByRole("region", { name: msg(locale, "hr_integration.lifecycle.changes.title") }).getByText(msg(locale, "hr_integration.lifecycle.changes.empty"))).toBeVisible();
      await expectAccessible(page, testInfo, `hr-integration-${locale}`);

      const matching = page.getByRole("region", { name: msg(locale, "hr_integration.settings.title") });
      await matching.getByLabel(msg(locale, "hr_integration.settings.matchRule")).selectOption("employee_number,work_email");
      await matching.getByLabel(msg(locale, "hr_integration.settings.threshold")).fill("10");
      await matching.getByLabel(msg(locale, "hr_integration.settings.leadDays")).fill("3");
      await matching.getByLabel(msg(locale, "hr_integration.settings.rehire")).check();
      await matching.getByRole("button", { name: msg(locale, "hr_integration.settings.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "hr_integration.settings.saved") })).toBeVisible();

      // Saved again with the version the page now holds.
      await matching.getByLabel(msg(locale, "hr_integration.settings.threshold")).fill("12");
      await matching.getByRole("button", { name: msg(locale, "hr_integration.settings.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "hr_integration.settings.saved") })).toBeVisible();
      await page.reload();
      await expect(page.getByRole("region", { name: msg(locale, "hr_integration.settings.title") }).getByLabel(msg(locale, "hr_integration.settings.threshold"))).toHaveValue("12");
      await expect(page.getByRole("region", { name: msg(locale, "hr_integration.settings.title") }).getByLabel(msg(locale, "hr_integration.settings.leadDays"))).toHaveValue("3");

      // Both HR packs are in the catalogue, each with its own add form.
      await page.getByRole("link", { name: msg(locale, "hr_integration.status.addConnection") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "connections.title"));
      await expect(page.getByText(msg(locale, "connections.catalogue.hr_rest"))).toBeVisible();
      await expect(page.getByRole("region", { name: msg(locale, "connections.hr.addRestTitle") }).getByLabel(msg(locale, "connections.hr.employeesPath"))).toBeVisible();
      const sftp = page.getByRole("region", { name: msg(locale, "connections.hr.addSftpTitle") });
      await expect(sftp.getByLabel(msg(locale, "connections.hr.fields.record_id"))).toBeVisible();
      await expectAccessible(page, testInfo, `hr-connections-${locale}`);
    });
  });
}
