import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 T012 in the browser: a service owner opens service analytics, finds their service and sees its figures for a
 * request they filed; the reportable reason shows as a table. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`service analytics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`san-${locale}`, locale, "workplace_plus");
    });

    test("an owner follows a service", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const names = { en: "Resignation", ar: "الاستقالة" };
      const created = await api.post("/api/v1/services/admin", { data: { kind: "form", names } });
      expect(created.status()).toBe(201);
      const id = ((await created.json()) as { service: { id: string } }).service.id;
      const labels = (en: string, ar: string) => ({ en, ar });
      const form = {
        fields: [
          {
            key: "reason",
            type: "choice",
            labels: labels("Reason", "السبب"),
            required: true,
            reportable: true,
            options: [
              { value: "pay", labels: labels("Pay", "الأجر") },
              { value: "growth", labels: labels("Growth", "النمو") },
            ],
          },
        ],
      };
      expect((await api.put(`/api/v1/services/admin/${id}/draft`, { data: { form } })).status()).toBe(200);
      expect((await api.post(`/api/v1/services/admin/${id}/publish`, { data: {} })).status()).toBe(200);
      expect((await api.post("/api/v1/requests", { data: { serviceId: id, data: { reason: "pay" } } })).status()).toBe(201);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);
      await page.goto(`${origin}/analytics/services`);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.services.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-services-${locale}`);

      await page.getByRole("link", { name: names[locale] }).click();
      await page.waitForURL(/\/analytics\/services\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1, name: names[locale] })).toBeVisible();
      await expect(page.getByTestId("service-dashboard")).toBeVisible();
      // One request is under the minimum group size: its time is hidden.
      await expect(page.getByTestId("service-median")).toHaveText(m("analytics.services.hidden", { size: "5" }));
      await expect(page.getByTestId("service-reasons-reason")).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-service-${locale}`);
    });
  });
}
