import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 020 R2 (T016, US4): Agent Studio. An administrator builds a blank agent from its name and instructions, opens it
 * in the editor, sees the read-only definition, and clones it into a new draft. In English and Arabic with axe. The
 * API decides every rule; these steps check what the pages send and show.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent studio in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const unitName = locale === "ar" ? "المشتريات" : "Procurement";

    test.beforeAll(async () => {
      tenant = await createTenant(`studio-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "PROC", names: { en: "Procurement", ar: "المشتريات" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      await api.dispose();
    });

    test("a blank agent is built, shown as JSON and cloned", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents`);
      const studio = page.getByTestId("agents-studio");
      await expect(studio.getByRole("heading", { name: m("agents.studio.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `agent-studio-${locale}`);

      await studio.getByLabel(m("agents.studio.unit")).selectOption({ label: unitName });
      for (const lang of ["en", "ar"] as const) {
        const language = m(`agents.studio.languageNames.${lang}`);
        await studio.getByLabel(m("agents.studio.name", { language }), { exact: true }).fill(lang === "ar" ? "مساعد تسجيل الموردين" : "Supplier onboarding helper");
        await studio
          .getByLabel(m("agents.studio.instructions", { language }), { exact: true })
          .fill(lang === "ar" ? "ساعد الزملاء في تسجيل مورد جديد." : "Help colleagues register a new supplier.");
      }
      await studio.getByLabel(m("agents.studio.category")).selectOption({ label: m("agents.categories.requests") });
      await studio.getByRole("button", { name: m("agents.studio.create") }).click();
      await page.waitForURL(/\/admin\/agents\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(locale === "ar" ? "مساعد تسجيل الموردين" : "Supplier onboarding helper");
      await expect(page.getByTestId("agent-status")).toHaveText(m("agents.status.draft"));

      // The administrator also holds the AI policy permission, so the read-only JSON view is there.
      const json = page.getByTestId("agent-json");
      await json.getByText(m("agents.editor.json")).click();
      await expect(json.locator("pre")).toContainText('"autonomy": "L0"');
      await expectAccessible(page, testInfo, `agent-studio-editor-${locale}`);

      const clone = page.getByTestId("agent-clone");
      await clone.getByLabel(m("agents.editor.clone.unit")).selectOption({ label: unitName });
      for (const lang of ["en", "ar"] as const) {
        const field = m("agents.editor.inLanguage", { field: m("agents.editor.clone.name"), language: m(`agents.editor.languages.${lang}`) });
        await clone.getByLabel(field, { exact: true }).fill(lang === "ar" ? "نسخة المساعد" : "Helper copy");
      }
      const first = page.url();
      await clone.getByRole("button", { name: m("agents.editor.clone.action") }).click();
      await page.waitForURL((url) => /\/admin\/agents\/[0-9a-f-]{36}$/.test(url.pathname) && url.toString() !== first);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(locale === "ar" ? "نسخة المساعد" : "Helper copy");

      await page.goto(`${hostOrigin(tenant.key)}/admin/agents`);
      await expect(page.getByTestId("agents-registry").locator("tbody tr")).toHaveCount(2);
    });
  });
}
