import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 R2 US6 in the browser (T026): a communications administrator creates a site from a template, which gets its
 * home page in the page builder, finds it in the reader's list of sites, then archives and restores it. In English and
 * Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content sites in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const name = locale === "ar" ? "موقع الفروع" : "Branch site";

    test.beforeAll(async () => {
      tenant = await createTenant(`site-${locale}`, locale, "workplace_plus", []);
    });

    test("an administrator creates, archives and restores a site", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/sites`);
      await expect(page.getByRole("heading", { level: 1, name: m("content.sites.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `content-sites-${locale}`);

      await page.getByLabel(locale === "ar" ? m("content.sites.nameAr") : m("content.sites.nameEn")).fill(name);
      await page.getByLabel(m("content.sites.template")).first().selectOption("location");
      await page.getByLabel(m("content.sites.homeSlug")).fill(`branch-${locale}`);
      await page.getByRole("button", { name: m("content.sites.create"), exact: true }).click();
      await page.waitForURL(/\/content\/sites\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
      await expect(page.getByRole("link", { name: m("content.sites.editHome") })).toBeVisible();
      await expectAccessible(page, testInfo, `content-site-${locale}`);

      // A site for the whole organization reaches everyone, so it shows in the reader's list.
      const settings = page.url();
      await page.goto(`${hostOrigin(tenant.key)}/sites`);
      await expect(page.getByTestId("site-link").filter({ hasText: name })).toBeVisible();
      await expectAccessible(page, testInfo, `sites-mine-${locale}`);

      await page.goto(settings);
      await page.getByRole("button", { name: m("content.sites.archive") }).click();
      await page.getByRole("button", { name: m("content.sites.archiveConfirm"), exact: true }).click();
      await expect(page.getByText(m("content.sites.archivedNote"))).toBeVisible();
      await page.goto(`${hostOrigin(tenant.key)}/sites`);
      await expect(page.getByTestId("site-link").filter({ hasText: name })).toHaveCount(0);

      await page.goto(settings);
      await page.getByRole("button", { name: m("content.sites.restore") }).click();
      await expect(page.getByText(m("content.sites.archivedNote"))).toHaveCount(0);
    });
  });
}
