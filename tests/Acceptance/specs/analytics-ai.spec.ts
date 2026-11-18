import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const people = ["q1", "q2", "q3", "q4", "q5"];

/**
 * Spec 021 T017 in the browser: five people ask the assistant a question; the administrator opens AI usage from the
 * analytics workspace, sees the requests and the assistant questions, and asks for an AI summary of the dashboard,
 * which is labelled as AI-generated with the figures it used. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`AI usage analytics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`aiu-${locale}`, locale, "workplace_plus", people);
      for (const person of people) {
        const client = await tenantApiAs(tenant, `${person}@${tenant.key}.example`);
        const asked = await client.post("/api/v1/assistant/turns", { data: { question: "Where do I park?" } });
        expect(asked.ok(), await asked.text()).toBe(true);
        await client.dispose();
      }
    });

    test("an administrator reads AI usage and asks for a summary", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.aiUsageLink") }).click();
      await page.waitForURL(/\/analytics\/ai-usage$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.aiUsage.title") })).toBeVisible();
      await expect(page.getByTestId("ai-usage-total")).toContainText(m("analytics.aiUsage.requests"));
      const assistant = page.getByTestId("ai-usage-assistant");
      await expect(assistant).toContainText(m("analytics.aiUsage.questions"));
      await expect(assistant).not.toContainText(m("analytics.aiUsage.hidden", { size: "5" }));
      await expect(page.getByTestId("ai-usage-cases")).toContainText(m("analytics.aiUsage.useCase.embed"));
      await expectAccessible(page, testInfo, `analytics-ai-usage-${locale}`);

      await page.getByRole("button", { name: m("analytics.reports.summary.action"), exact: true }).click();
      const dialog = page.getByRole("dialog", { name: m("analytics.reports.summary.title") });
      await expect(dialog.getByText(m("analytics.reports.summary.aiGenerated"), { exact: true })).toBeVisible({ timeout: 30_000 });
      await expect(dialog.getByRole("heading", { name: m("analytics.reports.summary.figures") })).toBeVisible();
      await expect(dialog.getByRole("listitem")).toHaveCount(1);
      await expectAccessible(page, testInfo, `analytics-ai-summary-${locale}`);
    });
  });
}
