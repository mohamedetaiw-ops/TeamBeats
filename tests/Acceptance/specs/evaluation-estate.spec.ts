import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 034 R3 T028: the estate trends page lists the agents the administrator can see with their latest evaluated
 * version, says when none has a result yet, and in a package without advanced evaluation says the page is not included.
 * In English and Arabic with axe. The API counts the agents and decides the package. All content is fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`evaluation estate trends in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let basic: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const knowledge = locale === "ar" ? "المعرفة المؤسسية" : "Enterprise Knowledge";

    test.beforeAll(async () => {
      tenant = await createTenant(`estate-${locale}`, locale, "enterprise_gov");
      basic = await createTenant(`estate-basic-${locale}`, locale, "workplace");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "KNOW", names: { en: "Knowledge office", ar: "مكتب المعرفة" } } });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const created = await api.post("/api/v1/agents", { data: { templateId: "enterprise_knowledge", orgUnitId: unitId } });
      expect(created.ok(), await created.text()).toBe(true);
      await api.dispose();
    });

    test("the estate lists the agents and says when none has a result", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/evaluation`);
      await page.getByRole("link", { name: m("evaluation.tabs.estate") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("evaluation.estate.title"));
      await expect(page.getByTestId("estate-summary")).toHaveText(m("evaluation.estate.summaryNone"));
      const row = page.getByTestId("estate-agents").getByRole("row").filter({ hasText: knowledge });
      await expect(row).toContainText(m("evaluation.estate.noDrop"));
      await expect(page.getByText(m("evaluation.dashboard.noResults"))).toBeVisible();
      await expectAccessible(page, testInfo, `evaluation-estate-${locale}`);
    });

    test("a package without advanced evaluation does not include the estate", async ({ page }) => {
      await signInWithDevForm(page, basic.key, basic.adminEmail, locale);
      await page.goto(`${hostOrigin(basic.key)}/admin/evaluation/estate`);
      await expect(page.getByRole("status").filter({ hasText: m("evaluation.estate.unavailable") })).toBeVisible();
    });
  });
}
