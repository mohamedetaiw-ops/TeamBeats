import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 012 R3 US9 in the browser (T018): with personalization from use turned on and agreed to, the result the person
 * opened most leads the results page and says it was ranked from their use. In English and Arabic, with axe.
 */
const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

async function publishNews(api: APIRequestContext, title: string, text: string): Promise<string> {
  const created = await api.post("/api/v1/content/items", { data: { type: "news", variants: { en: { title, body: body(text) } } } });
  expect(created.status(), await created.text()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  const published = await api.post(`/api/v1/content/items/${id}/publish`, { data: {} });
  expect(published.ok(), await published.text()).toBe(true);
  return id;
}

async function ok(call: Promise<{ ok(): boolean; text(): Promise<string> }>): Promise<void> {
  const response = await call;
  expect(response.ok(), await response.text()).toBe(true);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`personalized search in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let weak: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`srp-${locale}`, locale, "enterprise_gov");
      const api = await tenantApi(tenant);
      await publishNews(api, "Kayak kayak handbook", "Kayak kayak kayak, everything about it.");
      weak = await publishNews(api, "Notes on the kayak rollout", "A short note.");
      await expect
        .poll(async () => ((await (await api.get("/api/v1/search?q=kayak")).json()) as { total: number }).total, { timeout: 30_000 })
        .toBe(2);
      await ok(api.put("/api/v1/experience/behavior-policy", { data: { enabled: true, consentMode: "opt_in" } }));
      await ok(api.put("/api/v1/me/personalization/behavior", { data: { consented: true } }));
      for (let i = 0; i < 3; i++) {
        await ok(api.post("/api/v1/me/recents", { data: { path: `/content/read/${weak}`, label: "kayak" } }));
      }
      await api.dispose();
    });

    test("the result the person opened most leads and is marked", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/search?q=kayak`);
      await expect(page.locator(".search-result")).toHaveCount(2);
      const first = page.locator(".search-result").first();
      await expect(first.getByRole("link")).toHaveAttribute("href", `/content/read/${weak}`);
      await expect(first.getByText(msg(locale, "search.personalized"), { exact: true })).toBeVisible();
      await expect(page.locator(".search-result").nth(1).getByText(msg(locale, "search.personalized"), { exact: true })).toHaveCount(0);
      await expectAccessible(page, testInfo, `search-personalized-${locale}`);
    });
  });
}
