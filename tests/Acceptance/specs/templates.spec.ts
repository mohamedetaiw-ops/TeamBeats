import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 032 US5: an administrator copies a platform template, locks a region and releases it; a page made from it is
 * told when version 2 is released and moves to it through the template change. English and Arabic, with no critical
 * axe findings. Template names come from the API's own catalogue or from what the test typed.
 */
function apiText(locale: LaunchLocale, key: string): string {
  const file = fileURLToPath(new URL(`../../../src/BuildingBlocks/Localization/catalogues/${locale}.json`, import.meta.url));
  const value = (JSON.parse(readFileSync(file, "utf8")) as Record<string, string>)[key];
  if (!value) throw new Error(`Missing API message ${locale}:${key}`);
  return value;
}

const names = { en: "Team news", ar: "أخبار الفريق" } as const;

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`templates in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    let tenant: TestTenant;
    const id = `team-news-${locale}`;

    test.beforeAll(async () => {
      tenant = await createTenant(`templates-${locale}`, locale);
    });

    test("copies a platform template, locks a region and releases it", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin`);
      await page.getByRole("link", { name: msg(locale, "admin.nav.templates") }).click();
      await page.waitForURL(`${origin}/admin/templates`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "templates.title") })).toBeVisible();
      await expect(page.getByText(msg(locale, "templates.empty"))).toBeVisible();
      await expectAccessible(page, testInfo, `templates-empty-${locale}`);

      // The ID is checked by the API, which says why next to the field.
      await page.getByLabel(msg(locale, "templates.basedOn")).selectOption({ label: apiText(locale, "experience.template.t04") });
      await page.getByLabel(msg(locale, "templates.id")).fill("X");
      await page.getByLabel(msg(locale, "templates.nameEn")).fill(names.en);
      await page.getByLabel(msg(locale, "templates.nameAr")).fill(names.ar);
      await page.getByRole("button", { name: msg(locale, "templates.createAction") }).click();
      await expect(page.locator(".field-error").first()).toHaveText(apiText(locale, "validation.template.id_invalid"));

      await page.getByLabel(msg(locale, "templates.id")).fill(id);
      await page.getByRole("button", { name: msg(locale, "templates.createAction") }).click();
      await page.waitForURL(`${origin}/admin/templates/${id}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(names[locale]);
      await expectAccessible(page, testInfo, `template-designer-${locale}`);

      const region = page.locator("fieldset.builder-region-policy").first();
      await region.getByLabel(msg(locale, "templates.protection")).selectOption("protected");
      await page.getByRole("button", { name: msg(locale, "templates.saveDraft") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "templates.saved") })).toBeVisible();

      await page.getByRole("button", { name: msg(locale, "templates.release") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "templates.releasedNow") })).toBeVisible();
      await expect(page.getByTestId("template-versions")).toContainText(msg(locale, "templates.states.released"));
      await expect(page.getByRole("button", { name: msg(locale, "templates.release") })).toBeDisabled();

      await page.goto(`${origin}/admin/templates`);
      await expect(page.getByTestId("templates-table").getByRole("row").filter({ hasText: names[locale] })).toContainText(
        apiText(locale, "experience.template.t04"),
      );
    });

    test("tells a page owner about version 2 and applies it", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      const api = await tenantApi(tenant);
      const created = await createPage(api, id, `team/${locale}-news`);

      // Version 2 through the API: saving after a release starts it as a draft, then it is released.
      const template = (await (await api.get(`/api/v1/experience/templates/${id}`)).json()) as {
        latest: { names: Record<string, string>; variants: string[]; regions: unknown[] };
      };
      const saved = await api.put(`/api/v1/experience/templates/${id}/draft`, {
        data: { names: template.latest.names, variants: template.latest.variants, regions: template.latest.regions },
      });
      expect(saved.ok(), await saved.text()).toBeTruthy();
      expect((await api.post(`/api/v1/experience/templates/${id}/release`, { data: {} })).ok()).toBeTruthy();
      await api.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin/pages/${created.id}`);
      await expect(page.locator(".builder-facts")).toContainText(names[locale]);
      await expect(page.getByTestId("template-newer")).toHaveText(msg(locale, "templates.change.newer", { version: "2" }));
      await expectAccessible(page, testInfo, `template-change-${locale}`);

      await page.getByRole("button", { name: msg(locale, "templates.change.review") }).click();
      await expect(page.getByTestId("template-change-plan")).toContainText(msg(locale, "templates.change.allKept"));
      await page.getByRole("button", { name: msg(locale, "templates.change.apply") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "templates.change.applied") })).toBeVisible();
      await expect(page.getByTestId("template-newer")).toHaveCount(0);
    });
  });
}
