import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 016 R2 T024 in the browser (US8): an owner opens the route designer, adds an approval with an auto-approve rule
 * from the keyboard palette, sets a service level, tests the draft as a sample employee and publishes. Validation of
 * every step setting, branches and joins is covered by the integration suite. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`route designer in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`route-${locale}`, locale, "workplace_plus", ["layla"]);
    });

    test("an owner designs a route, tests it and publishes", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/services`);
      const create = page.getByRole("form", { name: m("services.admin.createTitle") });
      await create.getByLabel(m("services.admin.nameEn")).fill("Training");
      await create.getByLabel(m("services.admin.nameAr")).fill("تدريب");
      await create.getByRole("button", { name: m("services.admin.create") }).click();
      await page.waitForURL(/\/admin\/services\/[0-9a-f-]{36}$/);

      const form = page.getByRole("form", { name: m("services.admin.formTitle") });
      await form.getByRole("button", { name: m("services.admin.addField") }).click();
      const field = form.getByTestId("builder-field").first();
      await field.getByLabel(m("services.admin.fieldKey")).fill("cost");
      await field.getByLabel(m("services.admin.fieldType")).first().selectOption("number");
      await field.getByLabel(m("services.admin.inEnglish", { label: m("services.admin.fieldLabel") })).fill("Cost");
      await field.getByLabel(m("services.admin.inArabic", { label: m("services.admin.fieldLabel") })).fill("التكلفة");

      await form.getByRole("button", { name: m("services.admin.openDesigner") }).click();
      const designer = form.getByTestId("route-designer");
      await designer.getByRole("button", { name: m("services.designer.add", { type: m("services.designer.types.approval") }) }).click();
      await designer.getByLabel(m("services.designer.nameEn")).fill("Manager approval");
      await designer.getByLabel(m("services.designer.nameAr")).fill("موافقة المدير");
      await designer.getByLabel(m("services.designer.assignee")).selectOption("permission_at_unit");
      await designer.getByRole("button", { name: m("services.designer.addCondition"), exact: true }).click();
      await designer.getByLabel(m("services.designer.conditionOperator", { position: "1" })).selectOption("lt");
      await designer.getByLabel(m("services.designer.conditionValue", { position: "1" })).fill("500");

      const outline = designer.getByTestId("route-outline");
      const approvalName = locale === "ar" ? "موافقة المدير" : "Manager approval";
      await expect(outline.getByRole("listitem")).toHaveCount(2);
      await expect(outline.getByRole("listitem").first()).toContainText(approvalName);

      await form.getByTestId("route-settings").getByLabel(m("services.designer.serviceLevel")).fill("5");
      await form.getByRole("button", { name: m("services.admin.saveDraft") }).click();
      await expect(form.getByText(m("services.admin.draftSaved"))).toBeVisible();
      await expectAccessible(page, testInfo, `services-route-designer-${locale}`);

      const testPanel = page.getByTestId("route-test");
      await testPanel.getByLabel("Cost").or(testPanel.getByLabel("التكلفة")).fill("200");
      await testPanel.getByRole("button", { name: m("services.designer.runTest") }).click();
      const result = testPanel.getByTestId("route-test-result");
      await expect(result).toBeVisible();
      await expect(result).toContainText(m("services.designer.testEvents.auto_approved"));
      await expectAccessible(page, testInfo, `services-route-test-${locale}`);

      await form.getByRole("button", { name: m("services.admin.publish") }).click();
      await expect(form.getByText(m("services.admin.published"))).toBeVisible();
    });
  });
}
