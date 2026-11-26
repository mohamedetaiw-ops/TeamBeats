import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 R3 US11 in the browser (T041): on a plan with campaign automation a publisher plans a follow-up that goes a
 * number of days after the first step and a goal on the first item, starts the campaign and sees both on the plan and
 * the dashboard. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content campaign automation in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`cca-${locale}`, locale, "enterprise_gov");
    });

    test("a publisher plans a follow-up and a goal", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const ids: string[] = [];
      for (const title of locale === "ar" ? ["سجّل في المزايا", "تذكير بالتسجيل"] : ["Enrol in benefits", "Reminder to enrol"]) {
        const body = { blocks: [{ type: "paragraph", children: [{ text: title }] }] };
        const created = await api.post("/api/v1/content/items", { data: { type: "news", variants: { [locale]: { title, body } } } });
        expect(created.status()).toBe(201);
        const id = ((await created.json()) as { id: string }).id;
        expect((await api.post(`/api/v1/content/items/${id}/publish`, { data: {} })).status()).toBe(200);
        ids.push(id);
      }

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/campaigns`);
      const name = locale === "ar" ? "حملة المتابعة" : "Follow-up campaign";
      await page.getByLabel(m("content.campaigns.name")).fill(name);
      await page.getByRole("button", { name: m("content.campaigns.createSubmit") }).click();
      await page.waitForURL(/\/content\/campaigns\/[0-9a-f-]{36}$/);

      await page.getByRole("button", { name: m("content.campaigns.addStep") }).click();
      await page.getByRole("group", { name: m("content.campaigns.step", { step: "1" }) }).getByLabel(m("content.campaigns.item")).selectOption(ids[0]);
      await page.getByRole("button", { name: m("content.campaigns.addStep") }).click();
      const second = page.getByRole("group", { name: m("content.campaigns.step", { step: "2" }) });
      await second.getByLabel(m("content.campaigns.item")).selectOption(ids[1]);
      await second.getByLabel(m("content.campaigns.when")).selectOption("1");
      await expect(second.getByLabel(m("content.campaigns.at"))).toHaveCount(0);
      await second.getByLabel(m("content.campaigns.delayDays")).fill("5");

      const goal = page.getByTestId("campaign-goal");
      await goal.getByLabel(m("content.campaigns.goalOn")).check();
      await goal.getByLabel(m("content.campaigns.goalItem")).selectOption(ids[0]);
      await goal.getByLabel(m("content.campaigns.goalPercent")).fill("60");
      await page.getByRole("button", { name: m("content.campaigns.save") }).click();
      await expect(page.getByText(m("content.campaigns.saved"))).toBeVisible();
      await expect(second.getByLabel(m("content.campaigns.delayDays"))).toHaveValue("5");
      await expectAccessible(page, testInfo, `content-campaign-automation-plan-${locale}`);

      await page.getByRole("button", { name: m("content.campaigns.start") }).click();
      await expect(page.getByText(m("content.campaigns.status.scheduled"))).toBeVisible();
      await expect(page.getByTestId("campaign-goal-summary")).toContainText(m("content.campaigns.goalSummary", { percent: "60" }));
      await expect(page.getByTestId("campaign-goal-progress")).toBeVisible();
      await expect(page.getByTestId("campaign-step")).toHaveCount(2);
      await expectAccessible(page, testInfo, `content-campaign-automation-${locale}`);
    });
  });
}
