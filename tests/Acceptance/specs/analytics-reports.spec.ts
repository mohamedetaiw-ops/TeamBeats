import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 T016 in the browser: the administrator exports the workspace overview, waits for the background job and sees
 * the report with its download links, then schedules the overview weekly and finds the schedule on the reports page.
 * In English and Arabic, with axe on the report and the reports list.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`analytics reports in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`arp-${locale}`, locale, "enterprise_gov");
    });

    test("an administrator exports and schedules the overview", async ({ page }, testInfo) => {
      test.setTimeout(180_000);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);

      await page.goto(`${origin}/analytics`);
      await page.getByRole("button", { name: m("analytics.reports.export"), exact: true }).first().click();
      const exportDialog = page.getByRole("dialog", { name: m("analytics.reports.exportTitle") });
      await expect(exportDialog).toBeVisible();
      await exportDialog.getByLabel(m("analytics.reports.name")).fill("Activity");
      await exportDialog.getByRole("radio", { name: m("analytics.reports.formatCsv") }).check();
      await exportDialog.getByRole("button", { name: m("analytics.reports.exportSubmit") }).click();
      await expect(exportDialog.getByText(m("analytics.reports.exportQueued"))).toBeVisible();
      await exportDialog.getByRole("link", { name: m("analytics.reports.openReports") }).click();
      await page.waitForURL(/\/analytics\/reports\/[0-9a-f-]+$/);
      await expect(page.getByRole("heading", { level: 1, name: "Activity" })).toBeVisible();

      // The export is made in the background within a few seconds.
      await expect(async () => {
        await page.reload();
        await expect(page.getByRole("link", { name: m("analytics.reports.downloadCsv") })).toBeVisible({ timeout: 2_000 });
      }).toPass({ timeout: 90_000, intervals: [3_000] });
      await expect(page.getByTestId("analytics-report")).toContainText(m("analytics.reports.status.ready"));
      await expect(page.getByTestId("report-table-summary")).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-report-${locale}`);

      await page.goto(`${origin}/analytics`);
      await page.getByRole("button", { name: m("analytics.reports.schedule"), exact: true }).click();
      const scheduleDialog = page.getByRole("dialog", { name: m("analytics.reports.scheduleTitle") });
      await scheduleDialog.getByLabel(m("analytics.reports.name")).fill("Weekly activity");
      await scheduleDialog.getByRole("radio", { name: m("analytics.reports.cadenceWeekly") }).check();
      await scheduleDialog.getByRole("button", { name: m("analytics.reports.scheduleSubmit") }).click();
      await expect(scheduleDialog.getByText(m("analytics.reports.scheduled"))).toBeVisible();
      await scheduleDialog.getByRole("link", { name: m("analytics.reports.openReports") }).click();
      await page.waitForURL(/\/analytics\/reports$/);
      const schedules = page.getByRole("table", { name: m("analytics.reports.schedules") });
      await expect(schedules).toContainText("Weekly activity");
      await expect(schedules).toContainText(m("analytics.reports.cadenceWeekly"));
      await expect(page.getByRole("table", { name: m("analytics.reports.received") })).toContainText("Activity");
      await expectAccessible(page, testInfo, `analytics-reports-${locale}`);
    });
  });
}
