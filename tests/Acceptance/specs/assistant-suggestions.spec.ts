import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 014 R3 US9 in the browser (T019): on a news item the assistant panel suggests questions about it; picking one
 * asks it kept to the item and the answer cites it. The person turns suggestions off from the panel and back on from
 * their preferences. In English and Arabic, with axe.
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

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`assistant suggestions in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let item: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`asg-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      item = await publishNews(api, "Library opening hours", "The library opens at eight and closes at six on weekdays.");
      await expect
        .poll(async () => ((await (await api.get("/api/v1/search?q=library")).json()) as { total: number }).total, { timeout: 30_000 })
        .toBe(1);
      await api.dispose();
    });

    test("a suggested question is asked about the page and suggestions can be turned off and on", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/read/${item}`);
      await page.locator(".shell-bar").getByRole("button", { name: msg(locale, "shell.ask") }).click();
      const panel = page.getByRole("dialog", { name: msg(locale, "assistant.title") });
      const suggested = panel.getByTestId("assistant-suggestions");
      await expect(suggested.getByRole("heading", { name: msg(locale, "assistant.suggestions.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `assistant-suggestions-${locale}`);

      await suggested.getByRole("button", { name: msg(locale, "assistant.suggestions.label.summarize") }).click();
      const turn = panel.locator(".assistant-turn").first();
      await expect(turn.getByText(msg(locale, "assistant.suggestions.question.summarize", { title: "Library opening hours" }))).toBeVisible();
      await expect(turn.getByText(msg(locale, "assistant.generated"))).toBeVisible();
      await expect(panel.getByTestId("assistant-suggestions")).toHaveCount(0);

      // Off from the panel: a new conversation on the page offers nothing.
      await page.reload();
      await page.locator(".shell-bar").getByRole("button", { name: msg(locale, "shell.ask") }).click();
      await panel.getByRole("button", { name: msg(locale, "assistant.suggestions.turnOff") }).click();
      await expect(panel.getByTestId("assistant-suggestions")).toHaveCount(0);

      // Back on from the preferences page.
      await page.goto(`${hostOrigin(tenant.key)}/settings`);
      const choice = page.getByTestId("assistant-suggestion-choice").getByLabel(msg(locale, "preferences.assistantSuggestions.use"));
      await expect(choice).not.toBeChecked();
      await choice.click();
      await expect(choice).toBeChecked();
      await page.goto(`${hostOrigin(tenant.key)}/content/read/${item}`);
      await page.locator(".shell-bar").getByRole("button", { name: msg(locale, "shell.ask") }).click();
      await expect(panel.getByTestId("assistant-suggestions")).toBeVisible();
    });
  });
}
