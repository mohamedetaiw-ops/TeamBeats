import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 R3 US9 scenario 8 in the browser (T024): a tenant administrator proposes trying an earlier release of a page,
 * sees it wait for another administrator's approval, and stops it; a person agrees to take part from their preferences.
 * In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`experiments in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let slug: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`exp-${locale}`, locale, "enterprise_gov");
      const api = await tenantApi(tenant);
      slug = `exp-${Date.now().toString(36)}`;
      const created = await api.post("/api/v1/experience/pages", { data: { templateId: "T01", slug, orgUnitId: null } });
      expect(created.status(), await created.text()).toBe(201);
      const id = ((await created.json()) as { id: string }).id;
      for (const reason of ["first version", "second version"]) {
        const published = await api.post(`/api/v1/experience/pages/${id}/publish`, { data: { reason } });
        expect(published.ok(), await published.text()).toBe(true);
      }
      await api.dispose();
    });

    test("an experiment is proposed, waits for approval and is stopped, and a person agrees to take part", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/analytics/experiments`);
      await expect(page.getByRole("heading", { name: m("analytics.experiments.title"), level: 1 })).toBeVisible();
      await page.getByLabel(m("analytics.experiments.name")).fill("Shorter home");
      await page.getByRole("button", { name: m("analytics.experiments.propose") }).click();
      const card = page.locator("[data-testid^='experiment-']").filter({ hasText: "Shorter home" });
      await expect(card.getByText(m("analytics.experiments.status.proposed"))).toBeVisible();
      await expectAccessible(page, testInfo, `experiments-${locale}`);

      // The proposer may not approve their own experiment; the API says why.
      await card.getByRole("button", { name: m("analytics.experiments.approve") }).click();
      await expect(page.locator(".notice-error[role=alert]")).toBeVisible();
      await card.getByRole("button", { name: m("analytics.experiments.stop") }).click();
      await expect(card.getByText(m("analytics.experiments.status.stopped"))).toBeVisible();

      await page.goto(`${hostOrigin(tenant.key)}/settings`);
      const use = page.getByTestId("experiment-choice").getByLabel(m("preferences.experiments.use"));
      await expect(use).not.toBeChecked();
      await use.click();
      await expect(use).toBeChecked();
    });
  });
}
