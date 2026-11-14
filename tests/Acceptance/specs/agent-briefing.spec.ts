import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { publishAgentThroughApi, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 020 R2 (T019, US8): daily briefings. With scheduled agents allowed by the tenant and a briefing offered by the
 * agent, a person subscribes at a time on chosen days, changes it, and stops it. In English and Arabic with axe. The
 * worker that runs briefings is covered by the integration tests; these steps check what the page sends and shows.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent briefing in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let agentId: string;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const agentName = locale === "ar" ? "مساعد الصباح" : "Morning helper";

    test.beforeAll(async () => {
      test.setTimeout(150_000);
      tenant = await createTenant(`briefing-${locale}`, locale, "workplace_plus", ["rana"]);
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "OPS", names: { en: "Operations", ar: "العمليات" } } });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const me = (await (await api.get("/api/v1/me")).json()) as { id: string };
      const member = await api.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } });
      expect(member.ok(), await member.text()).toBe(true);
      const current = (await (await api.get("/api/v1/agents/policy")).json()) as { version: number; policy: Record<string, unknown> };
      const policy = await api.put("/api/v1/agents/policy", { data: { version: current.version, policy: { ...current.policy, scheduledAgents: true } } });
      expect(policy.ok(), await policy.text()).toBe(true);
      await api.dispose();
      agentId = await publishAgentThroughApi(tenant, `rana@${tenant.key}.example`, unitId, {
        name: { en: agentName, ar: agentName },
        briefing: { enabled: true, prompt: { en: "Summarize my pending tasks.", ar: "لخص مهامي المعلقة." } },
      });
    });

    test("a person subscribes to a daily briefing, changes it and stops it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/agents/${agentId}`);
      const briefing = page.getByTestId("agent-briefing");
      await expect(briefing).toContainText(m("agents.briefing.offer"));
      await expectAccessible(page, testInfo, `agent-briefing-${locale}`);
      await briefing.getByLabel(m("agents.briefing.time"), { exact: true }).fill("07:30");
      await briefing.getByRole("button", { name: m("agents.briefing.subscribe") }).click();
      await expect(briefing.getByRole("button", { name: m("agents.briefing.unsubscribe") })).toBeVisible();

      await briefing.getByLabel(m("agents.briefing.time"), { exact: true }).fill("09:00");
      await briefing.getByRole("button", { name: m("agents.briefing.change") }).click();
      await page.reload();
      await expect(page.getByTestId("agent-briefing").getByLabel(m("agents.briefing.time"), { exact: true })).toHaveValue("09:00");

      await page.getByTestId("agent-briefing").getByRole("button", { name: m("agents.briefing.unsubscribe") }).click();
      await expect(page.getByTestId("agent-briefing").getByRole("button", { name: m("agents.briefing.subscribe") })).toBeVisible();
    });
  });
}
