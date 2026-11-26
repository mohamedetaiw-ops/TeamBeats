import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/** Template names come from the API catalogue, not the web messages. */
function apiText(locale: LaunchLocale, key: string): string {
  const file = fileURLToPath(new URL(`../../../src/BuildingBlocks/Localization/catalogues/${locale}.json`, import.meta.url));
  return (JSON.parse(readFileSync(file, "utf8")) as Record<string, string>)[key] ?? key;
}

/**
 * Spec 032 R3 FR-023 in the browser (T034): on the new page screen the editor says what the page is for, the fitting
 * templates are marked with the reason, every template stays available and picking a marked one takes its variant.
 * In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`template recommendations in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string) => msg(locale, key);

    test.beforeAll(async () => {
      tenant = await createTenant(`trc-${locale}`, locale);
    });

    test("templates that fit the purpose are marked and all stay available", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/new`);
      const all = await page.locator("label.builder-template").count();
      const recommend = page.getByTestId("template-recommend");
      await recommend.getByLabel(m("builder.recommend.purpose")).selectOption("news");
      await recommend.getByLabel(m("builder.recommend.density")).selectOption("heavy");

      const editorial = page.locator("label.builder-template").filter({ has: page.getByRole("radio", { name: apiText(locale, "experience.template.t06") }) });
      await expect(editorial.getByText(m("builder.recommend.badge"))).toBeVisible();
      await expect(editorial).toContainText(m("builder.recommend.reasons.purpose"));
      await expect(page.locator("label.builder-template[data-recommended]")).toHaveCount(3);
      await expect(page.locator("label.builder-template")).toHaveCount(all);
      await expectAccessible(page, testInfo, `template-recommendations-${locale}`);

      await page.getByRole("radio", { name: apiText(locale, "experience.template.t06") }).check();
      await expect(page.getByLabel(m("builder.create.variant"))).toHaveValue("compact");
    });
  });
}
