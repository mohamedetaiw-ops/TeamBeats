import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 015 T008: the shell bell with the unread count, the panel and the full centre, reading a notification, the
 * employee's preferences and the administrator's settings with a test email, in English and Arabic with axe.
 * Notifications are created through the development host's sample endpoint.
 */
test.skip(stack.target === "remote", "The sample notification endpoint exists in the development host only.");

const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

async function sample(tenant: TestTenant, label: string, category = "announcement") {
  const api = await tenantApi(tenant);
  const response = await api.post("/api/v1/dev/notifications/sample", { data: { label, category } });
  expect(response.ok()).toBe(true);
  await api.dispose();
}

async function noHorizontalScroll(page: Page) {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`notifications in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`notify-${locale}`, locale);
    });

    test("the bell shows the unread count, the panel lists notifications and opening one marks it read", async ({ page }, testInfo) => {
      const label = unique("hr");
      await sample(tenant, label);
      await sample(tenant, unique("it"), "security_notice");
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      const bell = page.locator(".notification-bell-button");
      await expect(bell.locator(".notification-count")).toHaveText("2");
      await bell.click();
      const panel = page.locator(".notification-panel");
      await expect(panel.getByRole("heading", { name: msg(locale, "notifications.title") })).toBeVisible();
      await expect(panel.locator(".notification-entry")).toHaveCount(2);
      await expect(panel.locator(".notification-entry[data-mandatory]")).toHaveCount(1);
      await expectAccessible(page, testInfo, `notifications-panel-${locale}`);

      await page.keyboard.press("Escape");
      await expect(panel).toBeHidden();
      await expect(bell).toBeFocused();

      await bell.click();
      await panel.locator(".notification-entry", { hasText: label }).getByRole("link").click();
      await page.waitForURL(`${hostOrigin(tenant.key)}/`);
      await expect(page.locator(".notification-bell-button .notification-count")).toHaveText("1");
    });

    test("the centre filters, marks all read and fits a phone", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/notifications`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "notifications.title"));
      await expect(page.locator("main .notification-entry")).toHaveCount(2);
      const filters = page.getByRole("group", { name: msg(locale, "notifications.filter.label") });
      await filters.getByRole("button", { name: msg(locale, "notifications.filter.mandatory"), exact: true }).click();
      await expect(page.locator("main .notification-entry")).toHaveCount(1);
      await filters.getByRole("button", { name: msg(locale, "notifications.filter.all"), exact: true }).click();
      await expect(page.locator("main .notification-entry")).toHaveCount(2);
      await expectAccessible(page, testInfo, `notifications-centre-${locale}`);

      await page.locator("main").getByRole("button", { name: msg(locale, "notifications.readAll") }).click();
      await expect(page.locator("main .notification-entry[data-unread]")).toHaveCount(0);
      await page.reload();
      await expect(page.locator(".notification-bell-button .notification-count")).toHaveCount(0);

      await page.setViewportSize({ width: 375, height: 800 });
      await noHorizontalScroll(page);
    });

    test("an employee chooses email per category and quiet hours", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/notifications/preferences`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "notifications.preferences.title"));
      const comment = msg(locale, "notifications.category.comment");
      await page.getByLabel(msg(locale, "notifications.preferences.emailFor", { category: comment })).check();
      await page.getByLabel(msg(locale, "notifications.preferences.digestFor", { category: comment })).check();
      const security = msg(locale, "notifications.category.security_notice");
      await expect(page.getByLabel(msg(locale, "notifications.preferences.emailFor", { category: security }))).toBeDisabled();
      await page.getByLabel(msg(locale, "notifications.preferences.quietHoursOn")).check();
      await page.getByRole("button", { name: msg(locale, "notifications.preferences.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "notifications.preferences.saved") })).toBeVisible();
      await expectAccessible(page, testInfo, `notifications-preferences-${locale}`);

      await page.reload();
      await expect(page.getByLabel(msg(locale, "notifications.preferences.digestFor", { category: comment }))).toBeChecked();
      await expect(page.getByLabel(msg(locale, "notifications.preferences.quietHoursOn"))).toBeChecked();
      await page.setViewportSize({ width: 375, height: 800 });
      await noHorizontalScroll(page);
    });

    test("an administrator sets the sender name and sends a test email", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/notifications`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "notifications.settings.title"));
      await expect(page.getByText(msg(locale, "notifications.settings.relay.notInPlan"))).toBeVisible();
      await page.getByLabel(msg(locale, "notifications.settings.senderName")).fill("Contoso");
      await page.getByRole("button", { name: msg(locale, "notifications.settings.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "notifications.settings.saved") })).toBeVisible();
      await page.getByRole("button", { name: msg(locale, "notifications.settings.test.action") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "notifications.settings.test.sent") })).toBeVisible();
      await expectAccessible(page, testInfo, `notifications-settings-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/dev/mailbox`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "notifications.mailbox.title"));
      await expect(page.locator(".notification-mail").filter({ hasText: "Contoso" }).first()).toBeVisible();
      await expectAccessible(page, testInfo, `notifications-mailbox-${locale}`);
    });
  });
}
