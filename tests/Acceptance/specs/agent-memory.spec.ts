import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { publishAgentThroughApi, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 020 R2 (T018, US9): agent memory. With persistent memory allowed by the tenant and set on the agent, the person
 * allows the agent to remember, the agent keeps a note in a conversation, and the person reads, edits and deletes it,
 * then stops the agent remembering. The Agents page lists what agents remember. In English and Arabic with axe. The
 * fake model keeps the note when the message carries its tool marker; the API decides every rule.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent memory in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let agentId: string;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const agentName = locale === "ar" ? "مساعد التفضيلات" : "Preferences helper";
    const note = locale === "ar" ? "يفضل الإجابات باللغة العربية." : "Prefers answers in Arabic.";
    const edited = locale === "ar" ? "يعمل في مكتب الرياض." : "Works in the Riyadh office.";

    test.beforeAll(async () => {
      test.setTimeout(150_000);
      tenant = await createTenant(`memory-${locale}`, locale, "workplace_plus", ["rana"]);
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "PEOPLE", names: { en: "People", ar: "الموظفون" } } });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      const me = (await (await api.get("/api/v1/me")).json()) as { id: string };
      const member = await api.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } });
      expect(member.ok(), await member.text()).toBe(true);
      const current = (await (await api.get("/api/v1/agents/policy")).json()) as { version: number; policy: Record<string, unknown> };
      const policy = await api.put("/api/v1/agents/policy", { data: { version: current.version, policy: { ...current.policy, persistentMemory: true } } });
      expect(policy.ok(), await policy.text()).toBe(true);
      await api.dispose();
      agentId = await publishAgentThroughApi(tenant, `rana@${tenant.key}.example`, unitId, {
        name: { en: agentName, ar: agentName },
        memory: { mode: "persistent", retentionDays: 60 },
      });
    });

    test("a person allows an agent to remember, then reads, edits and deletes its notes", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/agents/${agentId}`);
      const memory = page.getByTestId("agent-memory");
      await expect(memory).toContainText(m("agents.memory.offer", { days: 60 }));
      await expectAccessible(page, testInfo, `agent-memory-offer-${locale}`);
      await memory.getByRole("button", { name: m("agents.memory.accept") }).click();
      await expect(memory.getByText(m("agents.memory.none"))).toBeVisible();

      // The agent keeps a note in a conversation.
      await page.getByLabel(m("agents.use.message"), { exact: true }).fill(`${note} [fake:tool:memory__remember] [fake:args:${JSON.stringify({ text: note })}]`);
      await page.getByRole("button", { name: m("agents.use.send") }).click();
      await expect(page.getByTestId("agent-answer")).toBeVisible();
      await page.reload();
      const items = page.getByTestId("agent-memory-items");
      await expect(items.locator("li")).toHaveCount(1);
      await expect(items).toContainText(note);
      await expectAccessible(page, testInfo, `agent-memory-items-${locale}`);

      await items.getByRole("button", { name: m("agents.memory.edit") }).click();
      await items.getByLabel(m("agents.memory.note"), { exact: true }).fill(edited);
      await items.getByRole("button", { name: m("agents.memory.save") }).click();
      await expect(items).toContainText(edited);

      // The Agents page lists the agent with its note.
      await page.goto(`${hostOrigin(tenant.key)}/agents`);
      await expect(page.getByTestId("agents-memory")).toContainText(agentName);
      await page.getByTestId("agents-memory").getByRole("link", { name: agentName }).click();
      await page.waitForURL(/\/agents\/[0-9a-f-]{36}$/);

      await page.getByTestId("agent-memory-items").getByRole("button", { name: m("agents.memory.delete") }).click();
      await expect(page.getByTestId("agent-memory").getByText(m("agents.memory.none"))).toBeVisible();
      await page.getByRole("button", { name: m("agents.memory.withdraw") }).click();
      await page.getByRole("button", { name: m("agents.memory.withdrawConfirm") }).click();
      await expect(page.getByTestId("agent-memory").getByRole("button", { name: m("agents.memory.accept") })).toBeVisible();
    });
  });
}
