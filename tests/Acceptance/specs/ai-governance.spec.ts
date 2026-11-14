import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { tenantApi } from "../support/pages";
import { hostOrigin } from "../support/stack";

/**
 * Spec 013 T016: the AI governance pages. An administrator on a plan with model choice sets a limit on Model policy,
 * reorders the assistant route on Routing rules (held for the release gate, then applied with a reason), reads AI usage
 * by model, and opens and closes a debug window, in English and Arabic with axe on every screen. The API decides
 * every rule; these steps check what the pages send and show.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`AI governance pages in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    const unitName = locale === "ar" ? "الخليج" : "Gulf";

    test.beforeAll(async () => {
      tenant = await createTenant(`ai-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "subsidiary", code: "GULF", names: { en: "Gulf", ar: "الخليج" } } });
      expect(unit.ok(), await unit.text()).toBe(true);
    });

    test("policy, routing, usage and debug windows", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("navigation", { name: m("admin.navLabel") }).getByRole("link", { name: m("admin.nav.ai") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.policy.title"));
      await expect(page.getByTestId("ai-catalogue").locator("tbody tr")).toHaveCount(3);
      await expect(page.getByTestId("ai-read-only")).toHaveCount(0);
      await expectAccessible(page, testInfo, `ai-policy-${locale}`);
      await page.getByLabel(m("ai.policy.limits.maxTokens")).fill("2000");
      await page.getByRole("button", { name: m("ai.policy.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.policy.saved") })).toBeVisible();

      // A change to the assistant route waits for the release gate, then applies with a reason.
      await page.getByRole("link", { name: m("ai.tabs.routing") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.routing.title"));
      const assistant = m("ai.routing.useCases.assistant");
      await page.getByRole("button", { name: m("ai.routing.up", { name: "GPT-4o", useCase: assistant }) }).click();
      await expectAccessible(page, testInfo, `ai-routing-${locale}`);
      await page.getByRole("button", { name: m("ai.routing.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.routing.held") })).toBeVisible();
      // The assistant has no suites of its own yet, so the gate reports it as not tested and the change waits for a reason.
      const pending = page.getByTestId("ai-pending-change");
      await expect(async () => {
        await page.reload();
        await expect(page.getByTestId("ai-pending-gate")).toHaveText(m("ai.routing.pending.gateStatus.incomplete"), { timeout: 2_000 });
      }).toPass({ timeout: 60_000 });
      await expect(pending.getByTestId("ai-pending-failures")).toBeVisible();
      await pending.getByLabel(m("ai.routing.pending.reason")).fill(locale === "ar" ? "اختبار جودة النموذج الأكبر للمساعد" : "Pilot the larger model for the assistant");
      await pending.getByRole("button", { name: m("ai.routing.pending.override") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.routing.pending.applied") })).toBeVisible();
      await expect(page.getByTestId("ai-pending-change")).toHaveCount(0);

      await page.getByRole("link", { name: m("ai.tabs.usage") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.usage.title"));
      await page.getByLabel(m("ai.usage.groupBy")).selectOption("model");
      await page.getByRole("button", { name: m("ai.usage.apply") }).click();
      await page.waitForURL(/groupBy=model/);
      await expect(page.getByRole("heading", { name: m("ai.usage.byGroup", { group: m("ai.usage.groups.model") }) })).toBeVisible();
      await expect(page.getByText(m("ai.usage.noDenied"))).toBeVisible();
      await expect(page.getByRole("heading", { name: m("ai.usage.forecast.title") })).toBeVisible();
      // The CSV export downloads a file for the chosen period (US4 scenario 4).
      const [download] = await Promise.all([page.waitForEvent("download"), page.getByRole("link", { name: m("ai.usage.export") }).click()]);
      expect(download.suggestedFilename()).toMatch(/^ai-usage-.*\.csv$/);
      await expectAccessible(page, testInfo, `ai-usage-${locale}`);

      await page.getByRole("link", { name: m("ai.tabs.debug") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.debug.title"));
      await page.getByLabel(m("ai.debug.hours")).fill("2");
      await page.getByRole("button", { name: m("ai.debug.openButton") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.debug.opened", { useCase: assistant }) })).toBeVisible();
      await expect(page.getByTestId("ai-debug-windows").locator("tbody tr")).toHaveCount(1);
      await expectAccessible(page, testInfo, `ai-debug-${locale}`);
      await page.getByRole("button", { name: m("ai.debug.close", { useCase: assistant }) }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.debug.closed", { useCase: assistant }) })).toBeVisible();
      await page.getByRole("link", { name: m("ai.debug.view", { useCase: assistant }) }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.debug.entriesTitle"));
      await expect(page.getByText(m("ai.debug.noEntries"))).toBeVisible();
      await expectAccessible(page, testInfo, `ai-debug-entries-${locale}`);
    });

    // Spec 013 T020: a per person daily request limit is added, changed to alert only and removed.
    test("budgets", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/ai/budgets`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.budgets.title"));
      await expect(page.getByText(m("ai.budgets.emptyTitle"))).toBeVisible();
      await expectAccessible(page, testInfo, `ai-budgets-empty-${locale}`);

      const form = page.getByTestId("ai-budget-form");
      await form.getByLabel(m("ai.budgets.scope")).selectOption("each_user");
      await form.getByLabel(m("ai.budgets.period")).selectOption("day");
      await form.getByLabel(m("ai.budgets.kind")).selectOption("requests");
      await form.getByLabel(m("ai.budgets.requests"), { exact: true }).fill("50");
      await form.getByLabel(m("ai.budgets.action"), { exact: true }).selectOption("block");
      await form.getByRole("button", { name: m("ai.budgets.add") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.budgets.created") })).toBeVisible();
      const who = m("ai.budgets.scopes.each_user");
      const row = page.getByRole("row").filter({ hasText: who });
      await expect(row).toContainText(m("ai.budgets.perPerson"));
      await expect(row).toContainText(m("ai.budgets.actions.block"));
      await expectAccessible(page, testInfo, `ai-budgets-${locale}`);

      await page.getByRole("button", { name: m("ai.budgets.change", { who }) }).click();
      await form.getByLabel(m("ai.budgets.action"), { exact: true }).selectOption("alert");
      await form.getByRole("button", { name: m("ai.budgets.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.budgets.saved") })).toBeVisible();
      await expect(row).toContainText(m("ai.budgets.actions.alert"));

      await page.getByRole("button", { name: m("ai.budgets.remove", { who }) }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.budgets.removed") })).toBeVisible();
      await expect(page.getByText(m("ai.budgets.emptyTitle"))).toBeVisible();
    });

    // Spec 013 T022: a unit narrows the models it may use, then follows the organization policy again.
    test("unit policies", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/ai/units`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.units.title"));
      await expect(page.getByText(m("ai.units.emptyTitle"))).toBeVisible();
      await page.getByLabel(m("ai.units.chooseUnit")).selectOption({ label: unitName });
      await page.getByRole("button", { name: m("ai.units.open"), exact: true }).click();
      const editor = page.getByTestId("ai-unit-policy");
      await expect(editor.getByRole("heading", { name: m("ai.units.editTitle", { unit: unitName }) })).toBeVisible();
      await editor.getByLabel(m("ai.units.limitModels")).check();
      await editor.getByLabel("openai/gpt-4o", { exact: true }).uncheck();
      await expectAccessible(page, testInfo, `ai-units-${locale}`);
      await editor.getByRole("button", { name: m("ai.units.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.units.saved", { unit: unitName }) })).toBeVisible();
      await expect(page.getByRole("row").filter({ hasText: unitName })).toBeVisible();

      await editor.getByRole("button", { name: m("ai.units.remove") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.units.removed", { unit: unitName }) })).toBeVisible();
      await expect(page.getByText(m("ai.units.emptyTitle"))).toBeVisible();
    });

    test("private endpoints", async ({ page }, testInfo) => {
      const name = locale === "ar" ? "مجموعة الرياض" : "Riyadh cluster";
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/ai/endpoints`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("ai.endpoints.title"));
      await expect(page.getByText(m("ai.endpoints.emptyTitle"))).toBeVisible();
      const form = page.getByTestId("ai-endpoint-form");
      await form.getByLabel(m("ai.endpoints.name"), { exact: true }).fill(name);
      await form.getByLabel(m("ai.endpoints.baseUrl"), { exact: true }).fill("https://llm.private.example/v1");
      await form.getByLabel(m("ai.endpoints.secretRef"), { exact: true }).fill(`secret://tenants/${tenant.id}/llm-key`);
      await form.getByLabel(m("ai.endpoints.region"), { exact: true }).fill("sa");
      await form.getByLabel(m("ai.endpoints.modelName"), { exact: true }).fill("llama-3.1-70b");
      await form.getByLabel(m("ai.endpoints.contextTokens"), { exact: true }).fill("128000");
      await expectAccessible(page, testInfo, `ai-endpoints-${locale}`);
      await form.getByRole("button", { name: m("ai.endpoints.add") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.endpoints.created", { name }) })).toBeVisible();

      // The key is not in this stack's secret manager, so the test fails and names why; the endpoint stays off.
      await page.getByRole("button", { name: m("ai.endpoints.testFor", { name }) }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.endpoints.testFailed", { name }) })).toBeVisible();
      await expect(page.getByTestId("ai-endpoint-checks")).toContainText(m("ai.endpoints.checks.secret_missing"));
      await page.getByRole("button", { name: m("ai.endpoints.enableFor", { name }) }).click();
      await expect(page.locator(".notice-error[role=alert]")).toBeVisible();
      await expectAccessible(page, testInfo, `ai-endpoints-tested-${locale}`);

      await page.getByRole("button", { name: m("ai.endpoints.removeFor", { name }) }).click();
      await expect(page.getByRole("status").filter({ hasText: m("ai.endpoints.removed", { name }) })).toBeVisible();
      await expect(page.getByText(m("ai.endpoints.emptyTitle"))).toBeVisible();
    });
  });
}
