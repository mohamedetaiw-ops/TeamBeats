import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Search (spec 012 T009): the shell search box suggests permitted items and opens the results page, which filters by
 * source, marks a best bet as recommended and offers no result help, in English and Arabic with axe; the search
 * administrator manages best bets and synonyms from the admin area.
 */
const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

async function publishNews(api: APIRequestContext, en: string, ar: string, text: string): Promise<string> {
  const created = await api.post("/api/v1/content/items", {
    data: { type: "news", variants: { en: { title: en, body: body(text) }, ar: { title: ar, body: body(ar) } } },
  });
  expect(created.status(), await created.text()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  const published = await api.post(`/api/v1/content/items/${id}/publish`, { data: {} });
  expect(published.ok(), await published.text()).toBe(true);
  return id;
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`search in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let shuttle: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`search-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      shuttle = await publishNews(api, "Shuttle timetable changes", "تغيير مواعيد الحافلة", "The evening shuttle leaves Gate 1 at 18:20.");
      await publishNews(api, "Canteen menu for the week", "قائمة المقصف للأسبوع", "Fresh salads every day this week.");
      // Indexing runs from the outbox; wait until the item is searchable.
      await expect
        .poll(async () => ((await (await api.get("/api/v1/search?q=shuttle")).json()) as { total: number }).total, { timeout: 30_000 })
        .toBe(1);
      const bet = await api.post("/api/v1/search/best-bets", { data: { query: "bus", source: "content", itemId: shuttle } });
      expect(bet.status(), await bet.text()).toBe(201);
      await api.dispose();
    });

    test("the search box suggests items and the results page filters and marks best bets", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const box = page.getByRole("searchbox", { name: msg(locale, "shell.search.label") });
      await box.fill("shutt");
      await expect(page.locator(".shell-search-results").getByRole("link", { name: /Shuttle timetable changes|تغيير مواعيد الحافلة/ }).first()).toBeVisible();
      await box.fill("shuttle");
      await box.press("Enter");
      await page.waitForURL(/\/search\?q=shuttle/);
      await expect(page.locator(".search-result")).toHaveCount(1);
      const filters = page.getByRole("navigation", { name: msg(locale, "search.filters") });
      await expect(filters.getByRole("link", { name: new RegExp(msg(locale, "search.kinds.content")) })).toBeVisible();
      await expectAccessible(page, testInfo, `search-results-${locale}`);

      // A best bet leads, marked as recommended, though the item never says "bus".
      await page.goto(`${hostOrigin(tenant.key)}/search?q=bus`);
      const first = page.locator(".search-result").first();
      await expect(first.getByText(msg(locale, "search.recommended"), { exact: true })).toBeVisible();
      await expect(first.getByRole("link")).toHaveAttribute("href", `/content/read/${shuttle}`);

      // Nothing found says so.
      await page.goto(`${hostOrigin(tenant.key)}/search?q=${encodeURIComponent("zebra quantum")}`);
      await expect(page.getByText(msg(locale, "search.none", { q: "zebra quantum" }))).toBeVisible();
    });

    test("the search administrator adds and removes synonyms", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "search.admin.title") })).toBeVisible();
      const words = page.getByLabel(msg(locale, "search.admin.synonyms.words"), { exact: true });
      await words.fill("coach, shuttle");
      await page.getByRole("button", { name: msg(locale, "search.admin.synonyms.add") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "search.admin.synonyms.added") })).toBeVisible();
      await expect(page.getByRole("button", { name: msg(locale, "search.admin.synonyms.remove", { words: "coach, shuttle" }) })).toBeVisible();
      await expectAccessible(page, testInfo, `search-admin-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/search?q=coach`);
      await expect(page.locator(".search-result").first().getByRole("link")).toHaveAttribute("href", `/content/read/${shuttle}`);

      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      await page.getByRole("button", { name: msg(locale, "search.admin.synonyms.remove", { words: "coach, shuttle" }) }).click();
      await expect(page.getByText(msg(locale, "search.admin.synonyms.empty"))).toBeVisible();
    });
  });
}
