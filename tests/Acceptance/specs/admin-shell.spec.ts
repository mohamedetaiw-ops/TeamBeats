import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * R0 screens beyond sign-in: admin shell (spec 006 US4), language switcher (spec 007 US1) and keyboard-only use
 * (spec 007 US5). Navigation sections come from the API, so these tests check what the server allowed.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`admin shell in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    test("the administrator opens administration and a section", async ({ page }, testInfo) => {
      const tenant = await createTenant(locale, locale);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      await page.getByRole("link", { name: msg(locale, "home.openAdmin") }).click();
      await expect(page).toHaveURL(`${hostOrigin(tenant.key)}/admin`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "admin.title") })).toBeVisible();
      const nav = page.getByRole("navigation", { name: msg(locale, "admin.navLabel") });
      await expect(nav.getByRole("link").first()).toBeVisible();
      await expectAccessible(page, testInfo, `admin-${locale}`);

      // Every page also offers Administration in the account menu to people with a section (spec 008 FR-062).
      await page.goto(`${hostOrigin(tenant.key)}/settings`);
      await page.getByRole("button", { name: msg(locale, "shell.account", { name: tenant.adminEmail }) }).click();
      await page.locator(".shell-account-panel").getByRole("link", { name: msg(locale, "shell.administration") }).click();
      await expect(page).toHaveURL(`${hostOrigin(tenant.key)}/admin`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "admin.title") })).toBeVisible();

      const first = nav.getByRole("link").first();
      const label = (await first.innerText()).trim();
      await first.click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(label);
      await expectAccessible(page, testInfo, `admin-section-${locale}`);
    });
  });
}

test.describe("language and keyboard", () => {
  test.use({ locale: "en-US" });

  test("the language switcher changes language and direction, and the choice persists", async ({ page }) => {
    const tenant = await createTenant("lang", "en");
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");

    // Language lives with the other personal settings in one place on every page (spec 008 FR-010).
    await page.getByRole("button", { name: msg("en", "shell.settings.open") }).click();
    await page.getByLabel(msg("en", "shell.settings.language")).selectOption("ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg("ar", "home.title", { name: tenant.adminEmail }));

    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
  });

  test("sign-in, opening administration and signing out work with the keyboard only", async ({ page }) => {
    const tenant = await createTenant("kbd", "en");
    await page.goto(`${hostOrigin(tenant.key)}/signin`);

    await page.keyboard.press("Tab");
    await expect(page.getByRole("link", { name: msg("en", "app.skipToContent") })).toBeFocused();
    await page.keyboard.press("Tab");
    await expect(page.getByLabel(msg("en", "signin.email"))).toBeFocused();
    await page.keyboard.type(tenant.adminEmail);
    await page.keyboard.press("Enter");
    await page.waitForURL(`${hostOrigin(tenant.key)}/`);

    const openAdmin = page.getByRole("link", { name: msg("en", "home.openAdmin") });
    await openAdmin.focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(`${hostOrigin(tenant.key)}/admin`);

    // Sign out sits in the account menu behind the avatar button.
    const account = page.getByRole("button", { name: new RegExp(`^${escapeRegExp(msg("en", "shell.account", { name: "" }))}`) });
    await account.focus();
    await page.keyboard.press("Enter");
    await expect(account).toHaveAttribute("aria-expanded", "true");
    await page.getByRole("button", { name: msg("en", "shell.signOut") }).focus();
    await page.keyboard.press("Enter");
    await page.waitForURL(`${hostOrigin(tenant.key)}/signin`);

    // The session is gone on the server, not only in the page: the home page sends the visitor back to sign-in.
    await page.goto(hostOrigin(tenant.key));
    await expect(page).toHaveURL(`${hostOrigin(tenant.key)}/signin`);
  });
});

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
