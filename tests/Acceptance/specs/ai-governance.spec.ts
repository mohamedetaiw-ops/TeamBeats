import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
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

    test.beforeAll(async () => {
      tenant = await createTenant(`ai-${locale}`, locale, "workplace_plus");
    });

    test("policy, routing, usage and debug windows", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("link", { name: m("admin.nav.ai") }).click();
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
  });
}
