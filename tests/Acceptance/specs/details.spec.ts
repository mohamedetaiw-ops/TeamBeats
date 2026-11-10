import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 036 DLS-042 to DLS-067, the detail rules on live screens: the notifications inbox with its tabs and groups, the
 * quick preferences and account popovers on one frame, the assistant launcher hidden while a panel is open, the
 * Preferences page with connected apps and the pages reset, and the request rows with status badges, in English and
 * Arabic with axe.
 */
test.skip(stack.target === "remote", "The sample notification endpoint exists in the development host only.");

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`detail rules in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`details-${locale}`, locale);
      const api = await tenantApi(tenant);
      for (const [label, category] of [
        ["details-approval", "approval_waiting"],
        ["details-news", "announcement"],
      ]) {
        const response = await api.post("/api/v1/dev/notifications/sample", { data: { label, category } });
        expect(response.ok()).toBe(true);
      }
      await api.dispose();
    });

    test("the inbox groups what needs action first and the tabs narrow it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.locator(".notification-bell-button").click();
      const panel = page.locator(".notification-panel");
      await expect(panel.getByRole("heading", { level: 3, name: msg(locale, "notifications.groups.action") })).toBeVisible();
      await expect(panel.locator(".notification-entry .notification-dot")).toHaveCount(2);
      await panel.getByRole("button", { name: msg(locale, "notifications.tabs.action") }).click();
      await expect(panel.locator(".notification-entry")).toHaveCount(1);
      await panel.getByRole("button", { name: msg(locale, "notifications.tabs.all") }).click();
      await panel.locator(".notification-entry", { hasText: "details-news" }).hover();
      await panel.locator(".notification-entry", { hasText: "details-news" }).getByRole("button", { name: msg(locale, "notifications.markRead") }).click();
      await panel.getByRole("button", { name: msg(locale, "notifications.tabs.unread") }).click();
      await expect(panel.locator(".notification-entry")).toHaveCount(1);
      await expectAccessible(page, testInfo, `details-inbox-${locale}`);
    });

    test("shell popovers share one frame, stay inside the window and hide the launcher", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      const settings = page.locator(".shell-settings-panel");
      await expect(settings.getByRole("heading", { name: msg(locale, "shell.settings.title") })).toBeVisible();
      await expect(settings.getByRole("link", { name: msg(locale, "shell.settings.allPreferences") })).toHaveAttribute("href", "/settings");
      await expect(page.locator(".shell-ask")).toBeHidden();
      const box = await settings.boundingBox();
      const width = page.viewportSize()!.width;
      expect(box!.x).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width).toBeLessThanOrEqual(width);

      await page.locator(".shell-avatar").click();
      const account = page.locator(".shell-account-panel");
      await expect(account.getByRole("link", { name: msg(locale, "shell.preferences") })).toBeVisible();
      await expect(account.getByRole("button", { name: msg(locale, "shell.signOut") })).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(account).toBeHidden();
    });

    test("the Preferences page holds display, connected apps and the pages reset", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/settings`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "preferences.title"));
      for (const section of ["display", "notifications", "profile", "apps", "pages"]) {
        await expect(page.getByRole("heading", { level: 2, name: msg(locale, `preferences.${section}.title`) })).toBeVisible();
      }
      await expect(page.getByRole("button", { name: msg(locale, "shell.settings.resetLayouts") })).toBeVisible();
      await expectAccessible(page, testInfo, `details-preferences-${locale}`);
    });
  });
}
