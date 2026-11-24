import { expect, signInWithDevForm, test } from "../support/fixtures";
import type { LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 036 T027 keyboard coverage of the calendar parts (DLS-071, DLS-072) on the component gallery: the week strip
 * and the month each keep one day in the tab order, the arrow keys move between days (left and right mirror in Arabic),
 * Home and End go to the ends of the week, up and down move a week in the month, and Enter selects the focused day.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`calendar parts by keyboard in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const forward = locale === "ar" ? "ArrowLeft" : "ArrowRight";
    const back = locale === "ar" ? "ArrowRight" : "ArrowLeft";

    test.beforeAll(async () => {
      tenant = await createTenant(`cal-keys-${locale}`, locale);
    });

    test("the week strip and the month move by day and by week", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/ui-gallery/components`);
      await expect(page.getByTestId("components-gallery")).toHaveAttribute("data-hydrated", "true");

      const strip = page.locator(".ui-cal-strip");
      const day = (date: string) => strip.locator(`[data-date="${date}"]`);
      await expect(strip.locator('[tabindex="0"]')).toHaveCount(1);
      await expect(day("2026-11-10")).toHaveAttribute("tabindex", "0");
      await day("2026-11-10").focus();
      await page.keyboard.press(forward);
      await expect(day("2026-11-11")).toBeFocused();
      await page.keyboard.press(back);
      await page.keyboard.press(back);
      await expect(day("2026-11-09")).toBeFocused();
      await page.keyboard.press(back);
      await expect(day("2026-11-09")).toBeFocused();
      await page.keyboard.press("End");
      await expect(day("2026-11-15")).toBeFocused();
      await expect(day("2026-11-15")).toHaveAttribute("tabindex", "0");
      await page.keyboard.press("Enter");
      await expect(day("2026-11-15")).toHaveAttribute("aria-pressed", "true");

      const month = page.locator(".ui-cal-month");
      const cell = (date: string) => month.locator(`[data-date="${date}"]`);
      await expect(month.locator('[tabindex="0"]')).toHaveCount(1);
      await expect(cell("2026-11-15")).toHaveAttribute("tabindex", "0");
      await cell("2026-11-15").focus();
      await page.keyboard.press("ArrowUp");
      await expect(cell("2026-11-08")).toBeFocused();
      await page.keyboard.press("Home");
      await expect(cell("2026-11-02")).toBeFocused();
      await page.keyboard.press("ArrowDown");
      await expect(cell("2026-11-09")).toBeFocused();
      await page.keyboard.press(forward);
      await expect(cell("2026-11-10")).toBeFocused();
      await page.keyboard.press(" ");
      await expect(cell("2026-11-10")).toHaveAttribute("aria-pressed", "true");
      await expect(day("2026-11-10")).toHaveAttribute("aria-pressed", "true");
    });
  });
}
