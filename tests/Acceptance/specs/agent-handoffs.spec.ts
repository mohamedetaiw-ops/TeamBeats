import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 020 R3 T026: in an Enterprise and Government tenant the template library has the advanced templates, with
 * Process Automation not ready under the default policy, and the agent editor shows a declared handoff with its target
 * and its topic per language. In English and Arabic with axe. The API decides readiness and checks each handoff when the
 * agent runs. All content is fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent handoffs in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let front = "";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const knowledge = locale === "ar" ? "المعرفة المؤسسية" : "Enterprise Knowledge";
    const legal = locale === "ar" ? "الشؤون القانونية والامتثال" : "Legal and Compliance";
    const automation = locale === "ar" ? "أتمتة الإجراءات" : "Process Automation";

    test.beforeAll(async () => {
      tenant = await createTenant(`handoffs-${locale}`, locale, "enterprise_gov");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "LEGAL", names: { en: "Legal affairs", ar: "الشؤون القانونية" } } });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const install = async (templateId: string) => {
        const created = await api.post("/api/v1/agents", { data: { templateId, orgUnitId: unitId } });
        expect(created.ok(), await created.text()).toBe(true);
        return (await created.json()) as { agent: { id: string }; definition: Record<string, unknown> };
      };
      const target = await install("enterprise_knowledge");
      const first = await install("legal_compliance");
      front = first.agent.id;
      const definition = { ...first.definition, handoffs: [{ agent: target.agent.id, topic: { en: "General company knowledge", ar: "المعرفة العامة عن الشركة" } }] };
      const saved = await api.put(`/api/v1/agents/${front}/draft`, { data: { revision: 1, definition } });
      expect(saved.ok(), await saved.text()).toBe(true);
      await api.dispose();
    });

    test("the advanced templates are listed and the editor shows a declared handoff", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents`);
      const library = page.getByTestId("agents-templates");
      await expect(library.getByRole("heading", { name: legal, exact: true })).toBeVisible();
      const automationCard = library.getByRole("listitem").filter({ has: page.getByRole("heading", { name: automation, exact: true }) });
      await expect(automationCard).toContainText(m("agents.templates.notReadyPolicy"));
      await expect(automationCard.getByRole("button", { name: m("agents.templates.installNamed", { name: automation }) })).toBeDisabled();

      await page.goto(`${hostOrigin(tenant.key)}/admin/agents/${front}`);
      const handoffs = page.getByTestId("agent-handoffs");
      await expect(handoffs.getByRole("heading", { name: m("agents.editor.handoffs.title") })).toBeVisible();
      await expect(handoffs.getByRole("listitem")).toHaveCount(1);
      await expect(handoffs.getByRole("listitem")).toContainText(knowledge);
      const topic = m("agents.editor.inLanguage", { field: m("agents.editor.handoffs.topic"), language: m("agents.editor.languages.ar") });
      await expect(handoffs.getByLabel(topic)).toHaveValue("المعرفة العامة عن الشركة");
      await expect(handoffs.getByRole("button", { name: m("agents.editor.handoffs.remove", { name: knowledge }) })).toBeVisible();
      await expectAccessible(page, testInfo, `agent-handoffs-${locale}`);
    });
  });
}
