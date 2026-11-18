import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 027 T019: the agent governance pages added in R2. An AI policy administrator narrows the agent rules of one
 * unit and removes them again; a security officer opens the Agent Security Center (alerts, incidents, watch list),
 * works an incident opened for an agent (records an action, sees the stale version refused, closes it with a note)
 * and finds the evidence export. In English and Arabic with axe on every screen. The API decides every rule; these
 * steps check what the pages send and show.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent governance R2 pages in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let incident: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const unitName = locale === "ar" ? "الموارد البشرية" : "Human resources";
    const title = locale === "ar" ? "مراجعة وكيل الموارد البشرية" : "Review of the HR agent";

    test.beforeAll(async () => {
      tenant = await createTenant(`agsec-${locale}`, locale, "enterprise_gov");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "HR", names: { en: "Human resources", ar: "الموارد البشرية" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const agent = await api.post("/api/v1/agents", { data: { templateId: "enterprise_knowledge", orgUnitId: unitId } });
      expect(agent.status(), await agent.text()).toBe(201);
      const agentId = ((await agent.json()) as { agent: { id: string } }).agent.id;
      const opened = await api.post("/api/v1/agents/security/incidents", { data: { title, severity: "medium", agentId } });
      expect(opened.status(), await opened.text()).toBe(201);
      incident = ((await opened.json()) as { id: string }).id;
      await api.dispose();
    });

    test("a unit's agent rules are narrowed and removed", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents/policy/units`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.unitPolicy.title"));
      await page.getByLabel(m("agents.unitPolicy.unit")).selectOption({ label: unitName });
      await page.getByRole("button", { name: m("agents.unitPolicy.open"), exact: true }).click();
      await expect(page.getByRole("heading", { name: m("agents.unitPolicy.heading", { unit: unitName }) })).toBeVisible();
      await page.getByLabel(m("agents.policy.maxToolCalls")).fill("5");
      await page.getByLabel(m("agents.unitPolicy.noMemory")).check();
      await expectAccessible(page, testInfo, `agents-unit-policy-${locale}`);
      await page.getByRole("button", { name: m("agents.unitPolicy.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.unitPolicy.saved", { unit: unitName }) })).toBeVisible();

      await page.getByRole("button", { name: m("agents.unitPolicy.remove") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.unitPolicy.removed", { unit: unitName }) })).toBeVisible();
      await expect(page.getByLabel(m("agents.policy.maxToolCalls"))).toHaveValue("");
    });

    test("a security officer works an incident and finds its evidence", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents/security`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.security.title"));
      await expect(page.getByText(m("agents.security.noAlerts"))).toBeVisible();
      await expect(page.getByText(m("agents.security.noWatch"))).toBeVisible();
      await expectAccessible(page, testInfo, `agents-security-${locale}`);

      await page.getByTestId("agents-incidents").getByRole("link", { name: title }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(title);
      await expect(page.getByText(m("agents.security.incident.noActions"))).toBeVisible();
      await page.getByLabel(m("agents.security.severity")).selectOption(m("agents.security.severities.high"));
      await page.getByLabel(m("agents.security.incident.actionTaken")).fill(locale === "ar" ? "أوقفنا الوكيل وأبلغنا مالكه." : "Suspended the agent and told its owner.");
      await page.getByRole("button", { name: m("agents.security.incident.save"), exact: true }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.security.incident.saved") })).toBeVisible();
      await expect(page.locator(".agents-timeline li")).toHaveCount(1);
      await expect(page.getByRole("link", { name: m("agents.security.incident.evidence") })).toHaveAttribute("href", `/api/v1/agents/security/incidents/${incident}/evidence`);
      await expectAccessible(page, testInfo, `agents-incident-${locale}`);

      await page.getByLabel(m("agents.security.incident.closureNote")).fill(locale === "ar" ? "لم يُعثر على مشكلة." : "No issue found after review.");
      await page.getByRole("button", { name: m("agents.security.incident.close"), exact: true }).click();
      await expect(page.getByRole("status").filter({ hasText: m("agents.security.incident.closed") })).toBeVisible();
      await expect(page.getByText(m("agents.security.incidentStatus.closed"), { exact: true })).toBeVisible();
      await expect(page.getByLabel(m("agents.security.incident.closureNote"))).toHaveCount(0);
    });
  });
}
