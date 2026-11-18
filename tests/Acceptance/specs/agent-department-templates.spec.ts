import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 020 R2 (T020): the department templates 7 to 12 in the template library of a Workplace Plus tenant. An
 * administrator starts an agent from Employee Onboarding, and Procurement, an L3 template, shows as not ready. In
 * English and Arabic with axe. The API decides which templates the package includes and which are ready.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`department agent templates in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const onboarding = locale === "ar" ? "تهيئة الموظفين الجدد" : "Employee Onboarding";
    const procurement = locale === "ar" ? "المشتريات" : "Procurement";

    test.beforeAll(async () => {
      tenant = await createTenant(`dept-agents-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "PEOPLE", names: { en: "People services", ar: "خدمات الموظفين" } } });
      expect(unit.ok(), await unit.text()).toBe(true);
      await api.dispose();
    });

    test("a department template is installed and an L3 one is not ready", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents`);
      const library = page.getByTestId("agents-templates");
      const procurementCard = library.getByRole("listitem").filter({ has: page.getByRole("heading", { name: procurement, exact: true }) });
      await expect(procurementCard).toContainText(m("agents.templates.notReady"));
      await expect(procurementCard.getByRole("button", { name: m("agents.templates.installNamed", { name: procurement }) })).toBeDisabled();
      await expectAccessible(page, testInfo, `agent-department-templates-${locale}`);

      await page.getByRole("button", { name: m("agents.templates.installNamed", { name: onboarding }) }).click();
      await expect(page).toHaveURL(/\/admin\/agents\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toContainText(onboarding);
    });
  });
}
