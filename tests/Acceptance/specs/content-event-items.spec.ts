import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 FR-018 R3 in the browser (T042): an author starts an event item on a company event of the calendar, and a
 * reader sees the event card with its time, place and registration next to the text. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content event items in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`cev-${locale}`, locale, "workplace_plus");
    });

    test("an event item shows its calendar event", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const start = new Date(Date.now() + 10 * 86_400_000);
      start.setUTCHours(6, 0, 0, 0);
      const eventTitle = locale === "ar" ? "دورة الإسعافات الأولية" : "First aid course";
      const created = await api.post("/api/v1/calendar/admin-events", {
        data: {
          titles: { [locale]: eventTitle },
          kind: "company",
          orgUnitId: null,
          allDay: false,
          start: start.toISOString(),
          end: new Date(start.getTime() + 3 * 3_600_000).toISOString(),
          timeZone: "Asia/Dubai",
          location: locale === "ar" ? "قاعة التدريب" : "Training room",
          rsvp: false,
          registration: true,
          capacity: 20,
          waitlist: false,
        },
      });
      expect(created.status(), await created.text()).toBe(201);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/items/new`);
      await page.getByLabel(m("content.items.type")).selectOption("event");
      await expect(page.getByLabel(m("content.items.event"))).toBeVisible();
      await expect(page.getByLabel(m("content.items.event")).locator("option")).toHaveCount(1);
      await expectAccessible(page, testInfo, `content-event-new-${locale}`);
      const title = locale === "ar" ? "تعلّم الإسعافات الأولية معنا" : "Learn first aid with us";
      await page.getByLabel(m("content.items.title")).fill(title);
      await page.getByRole("button", { name: m("content.items.create") }).click();
      await page.waitForURL(/\/content\/items\/[0-9a-f-]{36}/);
      const id = /\/content\/items\/([0-9a-f-]{36})/.exec(page.url())![1]!;
      expect((await api.post(`/api/v1/content/items/${id}/publish`, { data: {} })).status()).toBe(200);

      await page.goto(`${hostOrigin(tenant.key)}/content/read/${id}`);
      await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
      const card = page.getByTestId("content-event");
      await expect(card.getByRole("heading", { name: eventTitle })).toBeVisible();
      await expect(card).toContainText(locale === "ar" ? "قاعة التدريب" : "Training room");
      await expect(card.getByRole("link", { name: m("content.read.event.register") })).toBeVisible();
      await expectAccessible(page, testInfo, `content-event-read-${locale}`);
    });
  });
}
