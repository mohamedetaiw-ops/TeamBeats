import type { APIRequestContext } from "@playwright/test";
import {
  expect,
  expectAccessible,
  signInWithDevForm,
  test,
} from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Search (spec 012 T009): the shell search box suggests permitted items and opens the results page, which filters by
 * source, marks a best bet as recommended and offers no result help, in English and Arabic with axe; the search
 * administrator manages best bets and synonyms from the admin area, and proposes a retrieval change that the release
 * gate holds (no agent has retrieval cases yet) and applies it with a reason (T007).
 */
const body = (text: string) => ({
  blocks: [{ type: "paragraph", children: [{ text }] }],
});

async function publishNews(
  api: APIRequestContext,
  en: string,
  ar: string,
  text: string,
): Promise<string> {
  const created = await api.post("/api/v1/content/items", {
    data: {
      type: "news",
      variants: {
        en: { title: en, body: body(text) },
        ar: { title: ar, body: body(ar) },
      },
    },
  });
  expect(created.status(), await created.text()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  const published = await api.post(`/api/v1/content/items/${id}/publish`, {
    data: {},
  });
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
      shuttle = await publishNews(
        api,
        "Shuttle timetable changes",
        "تغيير مواعيد الحافلة",
        "The evening shuttle leaves Gate 1 at 18:20.",
      );
      await publishNews(
        api,
        "Canteen menu for the week",
        "قائمة المقصف للأسبوع",
        "Fresh salads every day this week.",
      );
      // Indexing runs from the outbox; wait until the item is searchable.
      await expect
        .poll(
          async () =>
            (
              (await (await api.get("/api/v1/search?q=shuttle")).json()) as {
                total: number;
              }
            ).total,
          { timeout: 30_000 },
        )
        .toBe(1);
      const bet = await api.post("/api/v1/search/best-bets", {
        data: { query: "bus", source: "content", itemId: shuttle },
      });
      expect(bet.status(), await bet.text()).toBe(201);
      await api.dispose();
    });

    test("the search box suggests items and the results page filters and marks best bets", async ({
      page,
    }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const box = page.getByRole("searchbox", {
        name: msg(locale, "shell.search.label"),
      });
      await box.fill("shutt");
      await expect(
        page
          .locator(".shell-search-results")
          .getByRole("link", {
            name: /Shuttle timetable changes|تغيير مواعيد الحافلة/,
          })
          .first(),
      ).toBeVisible();
      await box.fill("shuttle");
      await box.press("Enter");
      await page.waitForURL(/\/search\?q=shuttle/);
      await expect(page.locator(".search-result")).toHaveCount(1);
      const filters = page.getByRole("navigation", {
        name: msg(locale, "search.filters"),
      });
      await expect(
        filters.getByRole("link", {
          name: new RegExp(msg(locale, "search.kinds.content")),
        }),
      ).toBeVisible();
      await expectAccessible(page, testInfo, `search-results-${locale}`);

      // A best bet leads, marked as recommended, though the item never says "bus".
      await page.goto(`${hostOrigin(tenant.key)}/search?q=bus`);
      const first = page.locator(".search-result").first();
      await expect(
        first.getByText(msg(locale, "search.recommended"), { exact: true }),
      ).toBeVisible();
      await expect(first.getByRole("link")).toHaveAttribute(
        "href",
        `/content/read/${shuttle}`,
      );

      // Nothing found says so.
      await page.goto(
        `${hostOrigin(tenant.key)}/search?q=${encodeURIComponent("zebra quantum")}`,
      );
      await expect(
        page.getByText(msg(locale, "search.none", { q: "zebra quantum" })),
      ).toBeVisible();
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

    test("the search administrator adds and removes synonyms", async ({
      page,
    }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      await expect(
        page.getByRole("heading", {
          level: 1,
          name: msg(locale, "search.admin.title"),
        }),
      ).toBeVisible();
      const words = page.getByLabel(
        msg(locale, "search.admin.synonyms.words"),
        { exact: true },
      );
      await words.fill("coach, shuttle");
      await page
        .getByRole("button", { name: msg(locale, "search.admin.synonyms.add") })
        .click();
      await expect(
        page
          .getByRole("status")
          .filter({ hasText: msg(locale, "search.admin.synonyms.added") }),
      ).toBeVisible();
      await expect(
        page.getByRole("button", {
          name: msg(locale, "search.admin.synonyms.remove", {
            words: "coach, shuttle",
          }),
        }),
      ).toBeVisible();
      await expectAccessible(page, testInfo, `search-admin-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/search?q=coach`);
      await expect(
        page.locator(".search-result").first().getByRole("link"),
      ).toHaveAttribute("href", `/content/read/${shuttle}`);

      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      await page
        .getByRole("button", {
          name: msg(locale, "search.admin.synonyms.remove", {
            words: "coach, shuttle",
          }),
        })
        .click();
      await expect(
        page.getByText(msg(locale, "search.admin.synonyms.empty")),
      ).toBeVisible();
    });

    test("the search administrator proposes a retrieval change and applies it with a reason", async ({
      page,
    }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/search`);
      const section = page.getByRole("region", {
        name: msg(locale, "search.admin.retrieval.title"),
      });
      await expect(section).toBeVisible();
      await section
        .getByLabel(msg(locale, "search.admin.retrieval.topK"), { exact: true })
        .fill("6");
      await section
        .getByRole("button", {
          name: msg(locale, "search.admin.retrieval.submit"),
        })
        .click();
      await expect(
        section
          .getByRole("status")
          .filter({ hasText: msg(locale, "search.admin.retrieval.proposed") }),
      ).toBeVisible();

      // The shadow index is built and the gate has no retrieval case to run, so the change waits for a decision.
      const api = await tenantApi(tenant);
      await expect
        .poll(
          async () =>
            (
              (await (
                await api.get("/api/v1/search/retrieval-config")
              ).json()) as { open: { status: string } | null }
            ).open?.status,
          {
            timeout: 30_000,
          },
        )
        .toBe("held");
      await page.reload();
      await expect(
        section.getByText(msg(locale, "search.admin.retrieval.status.held"), {
          exact: false,
        }),
      ).toBeVisible();
      await expectAccessible(page, testInfo, `search-retrieval-${locale}`);
      await section
        .getByLabel(msg(locale, "search.admin.retrieval.reason"))
        .fill("No retrieval cases yet; checked by hand.");
      await section
        .getByRole("button", {
          name: msg(locale, "search.admin.retrieval.apply"),
        })
        .click();
      await expect(
        section
          .getByRole("status")
          .filter({ hasText: msg(locale, "search.admin.retrieval.applied") }),
      ).toBeVisible();
      const config = (await (
        await api.get("/api/v1/search/retrieval-config")
      ).json()) as { active: { version: number; topK: number } };
      expect(config.active).toMatchObject({ version: 1, topK: 6 });
      await api.dispose();
    });
  });
}
