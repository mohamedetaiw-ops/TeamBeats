import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 022 T007: the calendars admin page. The administrator creates a Sunday to Thursday calendar, adds a holiday,
 * assigns the calendar to a department and counts working days for that department, in English and Arabic with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`working calendars in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const unitName = locale === "ar" ? "الشؤون القانونية" : "Legal affairs";

    test.beforeAll(async () => {
      tenant = await createTenant(`cal-${locale}`, locale);
      const api = await tenantApi(tenant);
      const response = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "LEGAL", names: { en: "Legal affairs", ar: "الشؤون القانونية" } },
      });
      expect(response.ok()).toBe(true);
      await api.dispose();
    });

    test("an administrator sets a working week and holiday, assigns it to a unit and counts working days", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/calendars`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "calendars.title"));
      await expect(page.getByRole("button", { name: new RegExp(msg(locale, "calendars.tenantDefault")) })).toBeVisible();
      await expectAccessible(page, testInfo, `calendars-${locale}`);

      // A Sunday to Thursday week (ISO 7, 1 to 4).
      const form = page.locator("form", { has: page.getByRole("heading", { name: msg(locale, "calendars.new.title") }) });
      await form.getByLabel(msg(locale, "calendars.nameEn")).fill("Gulf week");
      await form.getByLabel(msg(locale, "calendars.nameAr")).fill("أسبوع الخليج");
      await form.getByLabel(msg(locale, "calendars.country")).fill("AE");
      const week = form.getByRole("group", { name: msg(locale, "calendars.week") });
      const friday = new Intl.DateTimeFormat(locale, { weekday: "long", timeZone: "UTC" }).format(new Date(Date.UTC(2026, 0, 9)));
      const sunday = new Intl.DateTimeFormat(locale, { weekday: "long", timeZone: "UTC" }).format(new Date(Date.UTC(2026, 0, 11)));
      await week.getByLabel(friday).uncheck();
      await week.getByLabel(sunday).check();
      await form.getByRole("button", { name: msg(locale, "calendars.new.create") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "calendars.created") })).toBeVisible();

      // The new calendar opens; add a holiday on Sunday 2026-12-06 for 2026.
      const editor = page.locator("section", { has: page.locator("#calendar-edit-title") });
      await expect(editor.locator("#calendar-edit-title")).toHaveText(locale === "ar" ? "أسبوع الخليج" : "Gulf week");
      await editor.getByLabel(msg(locale, "calendars.holidays.year"), { exact: true }).fill("2026");
      await editor.getByRole("button", { name: msg(locale, "calendars.holidays.add") }).click();
      await editor.getByLabel(msg(locale, "calendars.holidays.dateFor", { number: "1" })).fill("2026-12-06");
      await editor.getByLabel(msg(locale, "calendars.holidays.nameEnFor", { number: "1" })).fill("Founders day");
      await editor.getByLabel(msg(locale, "calendars.holidays.nameArFor", { number: "1" })).fill("يوم المؤسسين");
      await editor.getByRole("button", { name: msg(locale, "calendars.holidays.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "calendars.holidays.saved") })).toBeVisible();
      await expectAccessible(page, testInfo, `calendars-editor-${locale}`);

      // Assign the calendar to the department.
      const assign = page.getByLabel(msg(locale, "calendars.assign.calendarFor", { unit: unitName }));
      await assign.selectOption({ label: locale === "ar" ? "أسبوع الخليج" : "Gulf week" });
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "calendars.assign.saved") })).toBeVisible();

      // Sunday 6 to Saturday 12 December 2026: Sunday is a holiday, Friday and Saturday are off, so four working days.
      const preview = page.locator("section", { has: page.locator("#calendar-preview-title") });
      await preview.getByLabel(msg(locale, "calendars.assign.unit")).selectOption({ label: unitName });
      await preview.getByLabel(msg(locale, "calendars.preview.from"), { exact: true }).fill("2026-12-06");
      await preview.getByLabel(msg(locale, "calendars.preview.to"), { exact: true }).fill("2026-12-12");
      await preview.getByRole("button", { name: msg(locale, "calendars.preview.run") }).click();
      await expect(preview.locator(".calendars-answer")).toContainText(new Intl.NumberFormat(locale).format(4));
    });
  });
}
