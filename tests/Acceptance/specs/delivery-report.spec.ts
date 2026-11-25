import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { expectDetailConformant } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 015 US8 (R2): the delivery report of a critical announcement. The administrator opens it from the editor and sees
 * people reached and the in-app channel as counts, with no names. It passes axe in both languages. Status counts per
 * channel, the critical-only rule, permissions and the tenant boundary are covered by DeliveryReportTests.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`delivery report in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);
    let tenant: TestTenant;
    let id = "";
    const title = locale === "ar" ? "تغيير مسار الإخلاء" : "Evacuation route changed";

    test.beforeAll(async () => {
      tenant = await createTenant(`delivery-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const body = { blocks: [{ type: "paragraph", children: [{ text: title }] }] };
      const item = await api.post("/api/v1/content/items", {
        data: { type: "announcement", critical: { presentation: "banner" }, variants: { [locale]: { title, body } } },
      });
      expect(item.ok(), await item.text()).toBe(true);
      id = ((await item.json()) as { id: string }).id;
      const published = await api.post(`/api/v1/content/items/${id}/publish`, { data: { material: true } });
      expect(published.ok(), await published.text()).toBe(true);
      await api.dispose();
    });

    test("the editor links to the report, which shows counts by channel", async ({ page }, testInfo) => {
      test.setTimeout(120_000);
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/content/items/${id}`);
      await page.getByRole("link", { name: m("content.items.deliveryReport") }).click();
      await expect(page.getByRole("heading", { level: 1, name: m("content.delivery.heading", { title }) })).toBeVisible();
      await expect(page.getByText(m("content.delivery.recipients"))).toBeVisible();
      await expect(page.getByRole("rowheader", { name: m("content.delivery.channelName.in_app") })).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expectAccessible(page, testInfo, `delivery-report-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/content/items/[id]/delivery");
    });
  });
}
