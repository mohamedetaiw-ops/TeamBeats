import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 R2 US5 scenario 3 in the browser (T030): an editor asks for an AI translation of the other language, sees it
 * marked machine translated, and approves it. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content translation in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const other = locale === "ar" ? "en" : "ar";

    test.beforeAll(async () => {
      tenant = await createTenant(`ctr-${locale}`, locale, "workplace_plus");
    });

    test("an editor translates a draft with AI and approves it", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const title = locale === "ar" ? "افتتاح المقصف" : "Canteen opens";
      const body = { blocks: [{ type: "paragraph", children: [{ text: title }] }] };
      const created = await api.post("/api/v1/content/items", { data: { type: "news", variants: { [locale]: { title, body } } } });
      expect(created.status()).toBe(201);
      const id = ((await created.json()) as { id: string }).id;

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/items/${id}`);
      await page.getByRole("tab", { name: new RegExp(m(`content.items.language.${other}`)) }).click();
      await page.getByRole("button", { name: m("content.items.translate") }).click();
      await expect(page.getByTestId("machine-translated")).toBeVisible();
      await expect(page.getByRole("tab", { name: new RegExp(m("content.items.machineTranslated")) })).toBeVisible();
      await expectAccessible(page, testInfo, `content-translation-${locale}`);

      await page.getByRole("button", { name: m("content.items.approveTranslation") }).click();
      await expect(page.getByText(m("content.items.translationApproved"))).toBeVisible();
      await expect(page.getByTestId("machine-translated")).toHaveCount(0);
    });
  });
}
