import type { Page, TestInfo } from "@playwright/test";
import { expect, signInWithDevForm, test } from "../support/fixtures";
import { expectConformant, settle } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Overlays (spec 038 A11Y-016, WCAG 2.4.3, 2.1.2): a modal dialog, a drawer and the shell's assistant panel take focus,
 * keep Tab inside while open, pass the accessibility gate while open, and on Escape close and give focus back. A native
 * modal lets Tab pass to the browser's own controls between rounds (focus on no page element), which is allowed; what
 * must never happen is focus on a page element behind the overlay.
 */
const inside = (overlay: ReturnType<Page["getByRole"]>) =>
  overlay.evaluate((el) => {
    const active = document.activeElement;
    return !active || active === document.body || active === document.documentElement || el.contains(active);
  });
async function trapsFocus(page: Page, overlay: ReturnType<Page["getByRole"]>, label: string, testInfo: TestInfo) {
  await expect(overlay).toBeVisible();
  expect(await overlay.evaluate((el) => el.contains(document.activeElement)), `${label} takes focus`).toBe(true);
  for (let i = 0; i < 12; i++) {
    await page.keyboard.press("Tab");
    expect.soft(await inside(overlay), `Tab stays inside ${label}`).toBe(true);
  }
  for (let i = 0; i < 4; i++) {
    await page.keyboard.press("Shift+Tab");
    expect.soft(await inside(overlay), `Shift+Tab stays inside ${label}`).toBe(true);
  }
  await expectConformant(page, testInfo, `overlay-${label}`);
  await page.keyboard.press("Escape");
  await expect(overlay).toBeHidden();
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`overlays in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    test.beforeAll(async () => {
      tenant = await createTenant(`a11y-overlays-${locale}`, locale, "enterprise_gov");
    });

    test("the dialog and drawer keep focus inside and give it back", async ({ page }, testInfo) => {
      const t = (key: string) => msg(locale, `components_gallery.${key}`);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/ui-gallery/components`);
      await expect(page.locator('[data-testid="components-gallery"][data-hydrated="true"]')).toBeAttached();
      const overlays = page.locator("section:has(> h2#cg-overlays)");

      const invite = overlays.getByRole("button", { name: t("overlays.openDialog") });
      await invite.focus();
      await page.keyboard.press("Enter");
      await trapsFocus(page, page.getByRole("dialog", { name: t("overlays.dialogTitle") }), `dialog-${locale}`, testInfo);
      await expect(invite).toBeFocused();

      const filters = overlays.getByRole("button", { name: t("overlays.openDrawer") });
      await filters.focus();
      await page.keyboard.press("Enter");
      await trapsFocus(page, page.getByRole("dialog", { name: t("overlays.drawerTitle") }), `drawer-${locale}`, testInfo);
      await expect(filters).toBeFocused();
    });

    test("the assistant panel opens from the shell, keeps focus inside and gives it back", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      await settle(page);
      const ask = page.getByRole("button", { name: msg(locale, "shell.ask") });
      await ask.focus();
      await page.keyboard.press("Enter");
      await trapsFocus(page, page.getByRole("dialog", { name: msg(locale, "assistant.title") }), `assistant-${locale}`, testInfo);
      await expect(ask).toBeFocused();
    });
  });
}
