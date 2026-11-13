import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * The assistant (spec 014 T007): a question gets a streamed, cited answer from content the person can open, with
 * feedback; a question nothing answers says so; conversations can be deleted. In English and Arabic with axe.
 */
const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

async function publishNews(api: APIRequestContext, en: string, ar: string, enText: string, arText: string): Promise<string> {
  const created = await api.post("/api/v1/content/items", {
    data: { type: "news", variants: { en: { title: en, body: body(enText) }, ar: { title: ar, body: body(arText) } } },
  });
  expect(created.status(), await created.text()).toBe(201);
  const id = ((await created.json()) as { id: string }).id;
  const published = await api.post(`/api/v1/content/items/${id}/publish`, { data: {} });
  expect(published.ok(), await published.text()).toBe(true);
  return id;
}

const questions: Record<LaunchLocale, string> = {
  en: "When does the evening shuttle leave Gate 1?",
  ar: "متى تغادر الحافلة المسائية من البوابة؟",
};

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`assistant in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let shuttle: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`assistant-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      shuttle = await publishNews(
        api,
        "Evening shuttle timetable",
        "مواعيد الحافلة المسائية",
        "The evening shuttle leaves Gate 1 at 18:20.",
        "تغادر الحافلة المسائية من البوابة 1 الساعة 18:20.",
      );
      // Indexing runs from the outbox; wait until the item can be retrieved.
      await expect
        .poll(async () => ((await (await api.get("/api/v1/search?q=shuttle")).json()) as { total: number }).total, { timeout: 30_000 })
        .toBe(1);
      await api.dispose();
    });

    test("a question gets a cited answer, feedback, and a no evidence reply", async ({ page }, testInfo) => {
      page.on("dialog", (dialog) => void dialog.accept());
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/assistant`);
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "assistant.title") })).toBeVisible();
      await expect(page.getByText(msg(locale, "assistant.history.empty"))).toBeVisible();

      const box = page.getByLabel(msg(locale, "assistant.label"), { exact: true });
      await box.fill(questions[locale]);
      await page.getByRole("button", { name: msg(locale, "assistant.ask"), exact: true }).click();
      await page.waitForURL(/\/assistant\?c=/);

      const turn = page.locator(".assistant-turn").first();
      await expect(turn.getByText(msg(locale, "assistant.generated"))).toBeVisible();
      const sources = turn.locator(".assistant-sources");
      await expect(sources.getByRole("link", { name: /Evening shuttle timetable|مواعيد الحافلة المسائية/ })).toHaveAttribute("href", `/content/read/${shuttle}`);
      await expect(turn.locator(".assistant-marker").first()).toHaveAttribute("href", `/content/read/${shuttle}`);
      await turn.getByRole("button", { name: msg(locale, "assistant.feedback.helpful"), exact: true }).click();
      await expect(turn.getByText(msg(locale, "assistant.feedback.thanks"))).toBeVisible();
      await expectAccessible(page, testInfo, `assistant-${locale}`);

      // A follow-up nothing answers says so, in the same conversation.
      await box.fill("zebra quantum");
      await page.getByRole("button", { name: msg(locale, "assistant.ask"), exact: true }).click();
      await expect(page.getByText(msg(locale, "assistant.outcomes.no_evidence"))).toBeVisible();
      await expect(page.locator(".assistant-turn")).toHaveCount(2);

      // Deleting the conversation leaves no history.
      await page.getByRole("button", { name: msg(locale, "assistant.history.delete"), exact: true }).click();
      // The dialog asks first (spec 014 FR-023); the delete all action lives in a menu, not beside the list.
      const ask = page.getByRole("dialog", { name: msg(locale, "assistant.history.confirmOneTitle") });
      await expect(ask).toBeVisible();
      await expect(page.getByRole("button", { name: msg(locale, "assistant.history.deleteAll"), exact: true })).toHaveCount(0);
      await ask.getByRole("button", { name: msg(locale, "assistant.history.confirmDelete"), exact: true }).click();
      await page.waitForURL(/\/assistant$/);
      await expect(page.getByText(msg(locale, "assistant.history.empty"))).toBeVisible();
    });

    test("the page an item is on can be summarized, and pasted text rewritten", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/read/${shuttle}`);
      const actions = page.locator(".assistant-item-actions");
      await expect(actions.getByRole("heading", { name: msg(locale, "assistant.tools.itemTitle") })).toBeVisible();
      await actions.getByRole("button", { name: msg(locale, "assistant.tools.actions.summarize"), exact: true }).click();
      await expect(actions.getByText(msg(locale, "assistant.generated"))).toBeVisible();
      await expectAccessible(page, testInfo, `assistant-item-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/assistant`);
      const tools = page.locator(".assistant-tools");
      await tools.getByLabel(msg(locale, "assistant.tools.text"), { exact: true }).fill("pls send the report by friday thx");
      await tools.getByRole("button", { name: msg(locale, "assistant.tools.run"), exact: true }).click();
      await expect(tools.getByText(msg(locale, "assistant.generated"))).toBeVisible();
      await expect(tools.getByRole("button", { name: msg(locale, "assistant.tools.copy"), exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `assistant-tools-${locale}`);
    });
  });
}
