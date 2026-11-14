import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { publishAgentThroughApi, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 020 R2 (T017, US6): the agent marketplace. An agent made for the Legal unit is offered on request to everyone
 * else. A colleague outside Legal finds it in the marketplace and asks for it; the owner grants the request on the
 * agent's page; the colleague can then use it, favourites it, and finds it marked under Agents. In English and Arabic
 * with axe. The agent is published through the API, and the API decides every rule.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent marketplace in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let agentId: string;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const agentName = locale === "ar" ? "مساعد العقود" : "Contracts helper";
    const rana = () => `rana@${tenant.key}.example`;

    test.beforeAll(async () => {
      test.setTimeout(150_000);
      tenant = await createTenant(`market-${locale}`, locale, "workplace_plus", ["rana"]);
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "LEGAL", names: { en: "Legal", ar: "الشؤون القانونية" } } });
      expect(unit.ok(), await unit.text()).toBe(true);
      const unitId = ((await unit.json()) as { unit: { id: string } }).unit.id;
      await api.dispose();
      agentId = await publishAgentThroughApi(tenant, rana(), unitId, {
        name: { en: agentName, ar: agentName },
        audience: { scope: "org_units", orgUnits: [unitId], outside: "request_access" },
      });
    });

    test("a colleague asks for an offered agent, the owner grants it, and it becomes a favourite", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, rana(), locale);
      await page.goto(`${hostOrigin(tenant.key)}/agents/marketplace`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("agents.marketplace.title"));
      const tile = page.getByTestId("marketplace-agent").filter({ hasText: agentName });
      await expect(tile).toContainText(m("agents.marketplace.noRatings"));
      await expectAccessible(page, testInfo, `agent-marketplace-${locale}`);
      await tile.getByRole("button", { name: m("agents.marketplace.requestAccess") }).click();
      await expect(tile.getByText(m("agents.marketplace.requested"))).toBeVisible();

      // The owner decides on the agent's page.
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/agents/${agentId}#access`);
      const access = page.getByTestId("agent-access");
      await expect(access.locator("li")).toHaveCount(1);
      await expectAccessible(page, testInfo, `agent-access-${locale}`);
      await access.getByRole("button", { name: m("agents.access.grant") }).click();
      await expect(access.getByText(m("agents.access.none"))).toBeVisible();

      // The colleague can now use it and keeps it close.
      await signInWithDevForm(page, tenant.key, rana(), locale);
      await page.goto(`${hostOrigin(tenant.key)}/agents/marketplace`);
      await tile.getByRole("button", { name: m("agents.marketplace.favourite", { name: agentName }) }).click();
      await expect(tile.getByRole("button", { name: m("agents.marketplace.unfavourite", { name: agentName }) })).toHaveAttribute("aria-pressed", "true");
      await page.goto(`${hostOrigin(tenant.key)}/agents`);
      const mine = page.getByTestId("agents-available").locator("li").first();
      await expect(mine).toContainText(agentName);
      await expect(mine).toContainText(m("agents.marketplace.favourited"));
    });
  });
}
