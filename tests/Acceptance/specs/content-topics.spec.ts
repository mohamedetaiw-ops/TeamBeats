import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 009 R2 US9 scenario 3 and FR-017 in the browser (T028): a hashtag in a published item opens its topic page,
 * the reader follows the topic, and the following page lists it with the item. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`content topics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const tag = locale === "ar" ? "صحة" : "wellness";
    const title = locale === "ar" ? "أسبوع الصحة" : "Wellness week";

    test.beforeAll(async () => {
      tenant = await createTenant(`tpc-${locale}`, locale, "workplace");
    });

    test("a reader opens a topic from an item, follows it and finds it on the following page", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const body = { blocks: [{ type: "paragraph", children: [{ text: `${title} #${tag}` }] }] };
      const created = await api.post("/api/v1/content/items", { data: { type: "news", variants: { [locale]: { title, body } } } });
      expect(created.status()).toBe(201);
      const id = ((await created.json()) as { id: string }).id;
      expect((await api.post(`/api/v1/content/items/${id}/publish`, { data: { material: true } })).ok()).toBe(true);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/read/${id}`);
      await page.getByRole("list", { name: m("content.read.tags") }).getByRole("link", { name: `#${tag}` }).click();
      await page.waitForURL(/\/content\/topics\//);
      await expect(page.getByRole("heading", { level: 1, name: `#${tag}` })).toBeVisible();
      await expect(page.getByTestId("topic-item").filter({ hasText: title })).toBeVisible();
      await expectAccessible(page, testInfo, `content-topic-${locale}`);

      await page.getByRole("button", { name: m("content.topics.followNamed", { name: `#${tag}` }) }).click();
      await expect(page.getByRole("button", { name: m("content.topics.unfollowNamed", { name: `#${tag}` }) })).toBeVisible();

      await page.goto(`${hostOrigin(tenant.key)}/content/following`);
      await expect(page.getByRole("link", { name: `#${tag}` })).toBeVisible();
      await expect(page.getByTestId("following-item").filter({ hasText: title })).toBeVisible();
      await expectAccessible(page, testInfo, `content-following-${locale}`);
    });
  });
}
