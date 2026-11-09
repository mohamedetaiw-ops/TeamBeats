import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 T011: the brand manager edits tokens with the live preview in both modes and directions, shows contrast
 * checks in text and refuses blocking ones on save, uploads a logo, and the saved brand becomes the tenant theme. It
 * works in English and Arabic, at narrow widths, with no critical axe findings.
 */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "base64");

const paneAccent = (page: Page, mode: "light" | "dark", dir: "ltr" | "rtl") =>
  page
    .locator(`figure.brand-preview[data-mode="${mode}"][dir="${dir}"]`)
    .evaluate((el) => getComputedStyle(el).getPropertyValue("--tb-color-accent-base").trim());

const tokenInput = (page: Page, locale: LaunchLocale, token: string, mode: "light" | "dark") =>
  page.getByRole("textbox", { name: msg(locale, "brand.tokenInput", { token, mode: msg(locale, `brand.modes.${mode}`) }), exact: true });

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`brand manager in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`brand-${locale}`, locale);
    });

    test("edits tokens with a live preview, blocks low contrast, uploads a logo and publishes the theme", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("link", { name: msg(locale, "admin.nav.branding") }).click();
      await page.waitForURL(`${hostOrigin(tenant.key)}/admin/branding`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "brand.title") })).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expect(page.locator("figure.brand-preview")).toHaveCount(4);
      expect(await paneAccent(page, "light", "ltr")).toBe("#3b2fc9");

      // Live preview: the accent changes in the light panes of both directions; the dark panes keep their own value.
      await tokenInput(page, locale, "color.accent.base", "light").fill("#0f766e");
      await expect.poll(() => paneAccent(page, "light", "rtl")).toBe("#0f766e");
      expect(await paneAccent(page, "light", "ltr")).toBe("#0f766e");
      await tokenInput(page, locale, "color.accent.base", "dark").fill("#5eead4");
      await expect.poll(() => paneAccent(page, "dark", "ltr")).toBe("#5eead4");

      // A shell text color that fails contrast shows in the table as text and blocks saving.
      await tokenInput(page, locale, "color.shell.text", "light").fill("#3b3f8f");
      const blocking = page.getByTestId("contrast-table").locator('tr[data-severity="blocking"]').filter({ hasText: "color.shell.text" });
      await expect(blocking.first()).toBeVisible();
      await expect(blocking.first()).toContainText(msg(locale, "brand.contrast.blocking"));
      await page.getByRole("button", { name: msg(locale, "brand.save") }).click();
      await expect(page.getByTestId("brand-status")).toHaveText(msg(locale, "brand.blocked"));
      await tokenInput(page, locale, "color.shell.text", "light").fill("");
      await expect(page.getByTestId("contrast-table").locator('tr[data-severity="blocking"]')).toHaveCount(0);

      // Logo upload goes through the spec 001 checks and shows in every preview pane.
      const slot = msg(locale, "brand.assets.slots.logo");
      await page.getByLabel(msg(locale, "brand.assets.uploadFor", { slot }), { exact: true }).setInputFiles({ name: "logo.png", mimeType: "image/png", buffer: png });
      await expect(page.locator("img.brand-preview-logo")).toHaveCount(4);
      await page.getByLabel(msg(locale, "brand.assets.uploadFor", { slot }), { exact: true }).setInputFiles({
        name: "bad.svg",
        mimeType: "image/svg+xml",
        buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>'),
      });
      await expect(page.locator(`li[data-slot="logo"] [role="alert"]`)).toBeVisible();

      await page.getByRole("button", { name: msg(locale, "brand.save") }).click();
      await expect(page.getByTestId("brand-status")).toHaveText(msg(locale, "brand.saved", { version: "2" }));

      for (const width of [320, 375, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        const wide = await page.evaluate(() =>
          [...document.querySelectorAll("body *")]
            .filter((el) => el.getBoundingClientRect().right > document.documentElement.clientWidth + 1 || el.getBoundingClientRect().left < -1)
            .slice(0, 5)
            .map((el) => `${el.tagName}.${el.className}`),
        );
        expect(overflow, `horizontal overflow at ${width}px: ${wide.join(", ")}`).toBeLessThanOrEqual(0);
      }
      await expectAccessible(page, testInfo, `brand-manager-${locale}`);

      // The saved brand is the tenant theme on the next page.
      await page.goto(`${hostOrigin(tenant.key)}/`);
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--tb-color-accent-base").trim())).toBe("#0f766e");
    });
  });
}
