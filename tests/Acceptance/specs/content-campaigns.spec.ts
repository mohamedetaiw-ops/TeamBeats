import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 R2 US8 in the browser (T034): a publisher creates a campaign, plans a mandatory step with a live item, starts
 * it and sees the plan and the dashboard. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content campaigns in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`ccm-${locale}`, locale, "workplace_plus");
    });

    test("a publisher plans, starts and follows a campaign", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const title = locale === "ar" ? "التسجيل في المزايا" : "Benefits enrolment";
      const body = { blocks: [{ type: "paragraph", children: [{ text: title }] }] };
      const created = await api.post("/api/v1/content/items", { data: { type: "news", variants: { [locale]: { title, body } } } });
      expect(created.status()).toBe(201);
      const id = ((await created.json()) as { id: string }).id;
      expect((await api.post(`/api/v1/content/items/${id}/publish`, { data: {} })).status()).toBe(200);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/campaigns`);
      await expect(page.getByRole("heading", { level: 1, name: m("content.campaigns.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `content-campaigns-${locale}`);

      const name = locale === "ar" ? "حملة المزايا" : "Benefits campaign";
      await page.getByLabel(m("content.campaigns.name")).fill(name);
      await page.getByRole("button", { name: m("content.campaigns.createSubmit") }).click();
      await page.waitForURL(/\/content\/campaigns\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();

      await page.getByRole("button", { name: m("content.campaigns.addStep") }).click();
      const step = page.getByRole("group", { name: m("content.campaigns.step", { step: "1" }) });
      await step.getByLabel(m("content.campaigns.item")).selectOption(id);
      await step.getByLabel(m("content.campaigns.mandatory")).check();
      await page.getByRole("button", { name: m("content.campaigns.save") }).click();
      await expect(page.getByText(m("content.campaigns.saved"))).toBeVisible();
      await expectAccessible(page, testInfo, `content-campaign-plan-${locale}`);

      await page.getByRole("button", { name: m("content.campaigns.start") }).click();
      await expect(page.getByText(m("content.campaigns.status.scheduled"))).toBeVisible();
      await expect(page.getByTestId("campaign-step").filter({ hasText: m("content.campaigns.mandatoryLabel") })).toBeVisible();
      await expect(page.getByRole("table", { name: m("content.campaigns.reach") })).toBeVisible();
      await expectAccessible(page, testInfo, `content-campaign-${locale}`);
    });
  });
}
