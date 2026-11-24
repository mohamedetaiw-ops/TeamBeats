import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 R2 T018 (US3 scenario 3): the brand gives its title and logo per language, and when the person switches
 * language the shell title and logo switch with it. In English and Arabic, with axe.
 */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "base64");
const titles = { en: "Acme Gulf", ar: "أكمي الخليج" } as const;

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`brand by language in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`brl-${locale}`, locale);
    });

    test("the shell title and logo switch with the language", async ({ page }, testInfo) => {
      const other = locale === "ar" ? "en" : "ar";
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding`);
      const languages = page.getByTestId("brand-languages");
      await expect(languages).toBeVisible();
      const titleIn = (l: "en" | "ar") =>
        languages.getByRole("textbox", { name: msg(locale, "brand.assets.titleIn", { language: msg(locale, `locale.${l}`) }), exact: true });
      // The values are typed again until the editor has hydrated and keeps them.
      await expect(async () => {
        await titleIn("en").fill(titles.en);
        await expect(titleIn("en")).toHaveValue(titles.en, { timeout: 1_000 });
      }).toPass();
      await titleIn("ar").fill(titles.ar);
      const arabicLogo = msg(locale, "brand.assets.slotIn", { slot: msg(locale, "brand.assets.slots.logo"), language: msg(locale, "locale.ar") });
      await languages.getByLabel(msg(locale, "brand.assets.uploadFor", { slot: arabicLogo }), { exact: true }).setInputFiles({ name: "logo-ar.png", mimeType: "image/png", buffer: png });
      await expect(languages.locator('li[data-slot="logo.ar"] img.brand-asset-image')).toBeVisible();
      // The right-to-left preview panes take the Arabic logo.
      await expect(page.locator('figure.brand-preview[dir="rtl"] img.brand-preview-logo')).toHaveCount(2);
      await expectAccessible(page, testInfo, `brand-languages-${locale}`);
      await page.getByRole("button", { name: msg(locale, "brand.save") }).click();
      await expect(page.getByTestId("brand-status")).toHaveText(msg(locale, "brand.saved", { version: "2" }));

      await page.goto(`${hostOrigin(tenant.key)}/`);
      await expect(page.locator(".shell-tenant")).toHaveText(titles[locale]);
      await expect(page.getByTestId("shell-logo")).toHaveCount(locale === "ar" ? 1 : 0);

      await page.getByRole("button", { name: msg(locale, "shell.settings.open") }).click();
      await page.getByLabel(msg(locale, "shell.settings.language"), { exact: true }).selectOption(other);
      await expect(page.locator("html")).toHaveAttribute("lang", other);
      await expect(page.locator(".shell-tenant")).toHaveText(titles[other]);
      await expect(page.getByTestId("shell-logo")).toHaveCount(other === "ar" ? 1 : 0);
      await expect(page.getByRole("link", { name: msg(other, "shell.homeOf", { name: titles[other] }) })).toBeVisible();
    });
  });
}
