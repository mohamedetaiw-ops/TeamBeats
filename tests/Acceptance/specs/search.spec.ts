import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Search (spec 012 T009): the shell search box suggests permitted items and opens the results page, which filters by
 * source, marks a best bet as recommended and offers no result help, in English and Arabic with axe; the search
 * administrator manages best bets and synonyms from the admin area, and proposes a retrieval change that the release
 * gate holds (no agent has retrieval cases yet) and applies it with a reason (T007). A person saves a search with its
 * daily alert and turns the alert off or removes it from their saved searches (T015). The search administrator builds a
 * judged query set from graded results, the first click on a result is reported, and search words in analytics can be
 * turned off (T016).
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
      // The first click on a result is reported to the query log with its position (T016).
      const reported = page.waitForRequest((r) => r.url().endsWith("/api/v1/search/clicks") && r.method() === "POST");
      await page.locator(".search-result a").first().click();
      expect((await reported).postDataJSON()).toMatchObject({ position: 1 });
      await page.waitForURL(/\/content\/read\//);

      // A best bet leads, marked as recommended, though the item never says "bus".
      await page.goto(`${hostOrigin(tenant.key)}/search?q=bus`);
      const first = page.locator(".search-result").first();
      await expect(first.getByText(msg(locale, "search.recommended"), { exact: true })).toBeVisible();
      await expect(first.getByRole("link")).toHaveAttribute("href", `/content/read/${shuttle}`);

      // Nothing found says so.
      await page.goto(`${hostOrigin(tenant.key)}/search?q=${encodeURIComponent("zebra quantum")}`);
      await expect(page.getByText(msg(locale, "search.none", { q: "zebra quantum" }))).toBeVisible();
    });

    test("the search administrator turns SharePoint on and results show it in its own group", async ({
      page,
    }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      const sources = page.getByRole("region", {
        name: msg(locale, "search.admin.federated.title"),
      });
      const sharepoint = sources.getByRole("switch", {
        name: msg(locale, "search.admin.federated.source.sharepoint"),
      });
      await expect(sharepoint).not.toBeChecked();
      await sharepoint.click();
      await expect(sharepoint).toBeChecked();
      await expect(sources.getByRole("status")).toHaveText(
        msg(locale, "search.admin.federated.on", {
          source: msg(locale, "search.admin.federated.source.sharepoint"),
        }),
      );

      // The tenant has no Microsoft 365 connection here, so the live answer is given in its documented shape: one group
      // that answered, then one that SharePoint cut off at the time limit.
      let state = "ok";
      await page.route("**/api/v1/search/federated?**", (route) =>
        route.fulfill({
          contentType: "application/json",
          body: JSON.stringify({
            query: "shuttle",
            groups: [
              {
                source: "sharepoint",
                state,
                results:
                  state === "ok"
                    ? [
                        {
                          title: "Shuttle routes.pdf",
                          link: "https://fake-sharepoint.example/b!d/01",
                          summary: "shuttle in Shuttle routes.pdf…",
                          location: "HR policies",
                          mediaType: "application/pdf",
                          modifiedAt: new Date().toISOString(),
                        },
                      ]
                    : [],
              },
            ],
          }),
        }),
      );
      await page.goto(`${hostOrigin(tenant.key)}/search?q=shuttle`);
      const group = page.getByRole("region", {
        name: msg(locale, "search.federated.source.sharepoint"),
      });
      await expect(
        group.getByRole("link", { name: /Shuttle routes\.pdf/ }),
      ).toHaveAttribute("href", "https://fake-sharepoint.example/b!d/01");
      await expect(group.getByText("HR policies")).toBeVisible();
      await expectAccessible(page, testInfo, `search-federated-${locale}`);
      state = "timeout";
      await page.reload();
      await expect(
        group.getByText(msg(locale, "search.federated.timeout")),
      ).toBeVisible();
    });

    test("the search administrator turns AI summaries on and a summary cites its sources and takes feedback", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      const panel = page.getByRole("region", { name: msg(locale, "search.admin.summary.title") });
      const toggle = panel.getByRole("switch", { name: msg(locale, "search.admin.summary.switch") });
      await expect(toggle).not.toBeChecked();
      await toggle.click();
      await expect(toggle).toBeChecked();

      // The model's answer is given in the documented shape, so the page is checked without a model provider.
      await page.route("**/api/v1/search/summary?**", (route) =>
        route.fulfill({
          contentType: "application/json",
          body: JSON.stringify({
            state: "answered",
            turnId: "00000000-0000-0000-0000-000000000001",
            text: "The evening shuttle leaves Gate 1 at 18:20 [1].",
            citations: [{ n: 1, title: "Shuttle timetable changes", link: `/content/read/${shuttle}`, kind: "content" }],
          }),
        }),
      );
      let feedback: unknown = null;
      await page.route("**/api/v1/assistant/turns/*/feedback", (route) => {
        feedback = route.request().postDataJSON();
        return route.fulfill({ status: 204 });
      });
      await page.goto(`${hostOrigin(tenant.key)}/search?q=shuttle`);
      const summary = page.getByRole("region", { name: msg(locale, "search.summary.title") });
      await expect(summary.getByText("The evening shuttle leaves Gate 1 at 18:20 [1].")).toBeVisible();
      await expect(summary.getByRole("link", { name: "Shuttle timetable changes" })).toHaveAttribute("href", `/content/read/${shuttle}`);
      await expect(summary.getByText(msg(locale, "search.summary.generated"))).toBeVisible();
      await expectAccessible(page, testInfo, `search-summary-${locale}`);
      await summary.getByRole("button", { name: msg(locale, "search.summary.notHelpful") }).click();
      await expect(summary.getByRole("status")).toHaveText(msg(locale, "search.summary.thanks"));
      expect(feedback).toEqual({ helpful: false, reason: null });
    });

    test("a person saves a search with a daily alert and manages it from saved searches", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/search?q=canteen`);
      const save = page.getByTestId("search-save");
      await save.getByRole("button", { name: msg(locale, "search.save.action") }).click();
      await expect(save.getByRole("status")).toHaveText(msg(locale, "search.save.saved"));
      // Saving the same search again is refused with the API's message.
      await save.getByRole("button", { name: msg(locale, "search.save.action") }).click();
      await expect(save.getByRole("alert")).toBeVisible();

      await save.getByRole("link", { name: msg(locale, "search.save.list") }).click();
      await page.waitForURL(/\/search\/saved$/);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "search.saved.title") })).toBeVisible();
      const list = page.getByTestId("saved-searches");
      await expect(list.getByRole("link", { name: "canteen" })).toHaveAttribute("href", "/search?q=canteen");
      const alerts = list.getByRole("switch", { name: msg(locale, "search.saved.alertsLabel", { query: "canteen" }) });
      await expect(alerts).toBeChecked();
      await expectAccessible(page, testInfo, `saved-searches-${locale}`);
      await alerts.click();
      await expect(list.getByRole("status")).toHaveText(msg(locale, "search.saved.alertsOff", { query: "canteen" }));
      await expect(alerts).not.toBeChecked();
      await list.getByRole("button", { name: msg(locale, "search.saved.removeLabel", { query: "canteen" }) }).click();
      await expect(list.getByRole("status").filter({ hasText: msg(locale, "search.saved.removed", { query: "canteen" }) })).toBeVisible();
      await expect(list.getByText(msg(locale, "search.saved.emptyTitle"))).toBeVisible();
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

    test("the search administrator proposes a retrieval change and applies it with a reason", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      const section = page.getByRole("region", { name: msg(locale, "search.admin.retrieval.title") });
      await expect(section).toBeVisible();
      await section.getByLabel(msg(locale, "search.admin.retrieval.topK"), { exact: true }).fill("6");
      await section.getByRole("button", { name: msg(locale, "search.admin.retrieval.submit") }).click();
      await expect(section.getByRole("status").filter({ hasText: msg(locale, "search.admin.retrieval.proposed") })).toBeVisible();

      // The shadow index is built and the gate has no retrieval case to run, so the change waits for a decision.
      const api = await tenantApi(tenant);
      await expect
        .poll(async () => ((await (await api.get("/api/v1/search/retrieval-config")).json()) as { open: { status: string } | null }).open?.status, {
          timeout: 30_000,
        })
        .toBe("held");
      await page.reload();
      await expect(section.getByText(msg(locale, "search.admin.retrieval.status.held"), { exact: false })).toBeVisible();
      await expectAccessible(page, testInfo, `search-retrieval-${locale}`);
      await section.getByLabel(msg(locale, "search.admin.retrieval.reason")).fill("No retrieval cases yet; checked by hand.");
      await section.getByRole("button", { name: msg(locale, "search.admin.retrieval.apply") }).click();
      await expect(section.getByRole("status").filter({ hasText: msg(locale, "search.admin.retrieval.applied") })).toBeVisible();
      const config = (await (await api.get("/api/v1/search/retrieval-config")).json()) as { active: { version: number; topK: number } };
      expect(config.active).toMatchObject({ version: 1, topK: 6 });
      await api.dispose();
    });

    // A tenant of its own: a judged set takes part in every retrieval gate, and the retrieval change test needs a gate with no cases.
    test("the search administrator builds a judged query set from graded results", async ({ page }, testInfo) => {
      const own = await createTenant(`judged-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(own);
      await publishNews(api, "Shuttle timetable changes", "تغيير مواعيد الحافلة", "The evening shuttle leaves Gate 1 at 18:20.");
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "OPS", names: { en: "Operations", ar: "العمليات" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      await expect
        .poll(async () => ((await (await api.get("/api/v1/search?q=shuttle")).json()) as { total: number }).total, { timeout: 30_000 })
        .toBe(1);
      await api.dispose();
      await signInWithDevForm(page, own.key, own.adminEmail, locale);
      await page.goto(`${hostOrigin(own.key)}/admin/search`);
      const panel = page.getByRole("region", { name: msg(locale, "search.admin.judged.title") });
      await panel.getByLabel(msg(locale, "search.admin.judged.name"), { exact: true }).fill(`Commute searches ${locale}`);
      await panel.getByRole("button", { name: msg(locale, "search.admin.judged.createAction") }).click();
      await expect(panel.getByRole("status").filter({ hasText: msg(locale, "search.admin.judged.created") })).toBeVisible();

      const editor = page.getByTestId("judged-set-editor");
      await editor.getByLabel(msg(locale, "search.admin.judged.query"), { exact: true }).fill("shuttle");
      await editor.getByRole("button", { name: msg(locale, "search.admin.judged.find") }).click();
      const results = editor.getByRole("list", { name: msg(locale, "search.admin.judged.results") });
      const grade = results.getByRole("combobox").first();
      await grade.selectOption("3");
      await expectAccessible(page, testInfo, `judged-sets-${locale}`);
      await editor.getByRole("button", { name: msg(locale, "search.admin.judged.keepQuery") }).click();
      await expect(editor.getByRole("cell", { name: "shuttle" })).toBeVisible();
      await editor.getByRole("button", { name: msg(locale, "search.admin.judged.save") }).click();
      await expect(panel.getByRole("status").filter({ hasText: msg(locale, "search.admin.judged.saved", { version: 1 }) })).toBeVisible();

      // Search words in analytics are kept until a search administrator turns them off.
      const words = page.getByRole("region", { name: msg(locale, "search.admin.queryText.title") });
      const keep = words.getByRole("switch", { name: msg(locale, "search.admin.queryText.switch") });
      await expect(keep).toBeChecked();
      await keep.click();
      await expect(words.getByRole("status")).toHaveText(msg(locale, "search.admin.queryText.off"));
      await keep.click();
      await expect(words.getByRole("status")).toHaveText(msg(locale, "search.admin.queryText.on"));
    });
  });
}
