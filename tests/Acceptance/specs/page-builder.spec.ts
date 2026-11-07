import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 032 T018 (first part): the Pages section lists the editor's pages, and a new page is created from the template
 * chooser, whose templates carry a text description and their regions. Works in English and Arabic, with no critical
 * axe findings. Template names come from the API's own catalogue, never from the test.
 */
function apiText(locale: LaunchLocale, key: string): string {
  const file = fileURLToPath(new URL(`../../../src/BuildingBlocks/Localization/catalogues/${locale}.json`, import.meta.url));
  const value = (JSON.parse(readFileSync(file, "utf8")) as Record<string, string>)[key];
  if (!value) throw new Error(`Missing API message ${locale}:${key}`);
  return value;
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`page builder in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`builder-${locale}`, locale);
    });

    test("creates a page from the template chooser and lists it", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin`);
      await page.getByRole("link", { name: msg(locale, "admin.nav.pages") }).click();
      await page.waitForURL(`${origin}/admin/pages`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "builder.pages.title") })).toBeVisible();
      await expect(page.getByText(msg(locale, "builder.pages.empty"))).toBeVisible();
      await expectAccessible(page, testInfo, `pages-empty-${locale}`);

      await page.getByRole("link", { name: msg(locale, "builder.pages.create") }).click();
      await page.waitForURL(`${origin}/admin/pages/new`);
      const overview = page.getByRole("radio", { name: apiText(locale, "experience.template.t04") });
      await overview.check();
      // The chooser says in text what the preview shows.
      const card = page.locator("label.builder-template").filter({ has: overview });
      await expect(card).toContainText(apiText(locale, "experience.template.t04.description"));
      await expect(card).toContainText(msg(locale, "builder.regions.hero"));
      await expectAccessible(page, testInfo, `pages-new-${locale}`);

      const slug = page.getByLabel(msg(locale, "builder.create.slug"));
      await slug.fill("Not A Slug");
      await page.getByRole("button", { name: msg(locale, "builder.create.submit") }).click();
      await expect(page.locator(".field-error")).toHaveText(msg(locale, "builder.create.slugInvalid"));

      await slug.fill(`news/${locale}-overview`);
      await page.getByRole("button", { name: msg(locale, "builder.create.submit") }).click();
      await page.waitForURL(/\/admin\/pages\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(`/p/news/${locale}-overview`);
      await expect(page.getByText(msg(locale, "builder.pages.draftOnly"))).toBeVisible();
      await expect(page.locator(".builder-region")).toHaveCount(2);
      await expectAccessible(page, testInfo, `pages-detail-${locale}`);

      // The same address again is refused with the reason next to the field.
      await page.goto(`${origin}/admin/pages/new`);
      await page.getByLabel(msg(locale, "builder.create.slug")).fill(`news/${locale}-overview`);
      await page.getByRole("button", { name: msg(locale, "builder.create.submit") }).click();
      await expect(page.locator(".field-error")).toHaveText(msg(locale, "builder.create.slugTaken"));

      await page.goto(`${origin}/admin/pages`);
      const row = page.getByTestId("pages-table").getByRole("row").filter({ hasText: `/p/news/${locale}-overview` });
      await expect(row).toContainText(apiText(locale, "experience.template.t04"));
      await expect(row).toContainText(msg(locale, "builder.pages.wholeTenant"));
    });
  });
}
