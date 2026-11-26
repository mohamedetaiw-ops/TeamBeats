import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 T015 in the browser: with a published page and slow phone visits reported for it, the administrator opens
 * experience analytics from the workspace and sees the template and the flagged variant. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`experience analytics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`xan-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/experience/pages", { data: { templateId: "T01", slug: `slow-${locale}`, orgUnitId: null } });
      expect(created.status(), await created.text()).toBe(201);
      const pageId = ((await created.json()) as { id: string }).id;
      expect((await api.post(`/api/v1/experience/pages/${pageId}/publish`, { data: { reason: "first version" } })).ok()).toBe(true);
      const events = Array.from({ length: 6 }, () => ({ name: "page.vitals", fields: { pageId, release: 1, deviceClass: "mobile", theme: "beats", lcp: 4200, inp: 90, cls: 0.01 } }));
      expect((await api.post("/api/v1/analytics/events", { data: { events } })).status()).toBe(202);
      await api.dispose();
    });

    test("an administrator finds a slow page", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);
      await page.goto(`${origin}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.experienceLink") }).click();
      await page.waitForURL(/\/analytics\/experience$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.experience.title") })).toBeVisible();
      await expect(page.getByTestId("experience-templates")).toContainText("T01");
      await expect(page.getByTestId("experience-flags")).toContainText(m("analytics.experience.missed"));
      await expect(page.getByTestId("experience-vitals-device")).toContainText(m("analytics.experience.device.mobile"));
      // R3 (FR-024): the usefulness of personalization and recommendations, one row per surface.
      const usefulness = page.getByTestId("experience-recommendations");
      await expect(usefulness.getByRole("rowheader", { name: m("analytics.experience.surface.search") })).toBeVisible();
      await expect(usefulness.getByRole("rowheader", { name: m("analytics.experience.surface.assistant") })).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-experience-${locale}`);
    });
  });
}
