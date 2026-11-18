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

    // Spec 014 FR-001 and T007: the panel in the shell opens from Ask TeamBeats or Ctrl+/ on any page, streams a cited
    // answer, and hands the open conversation to the full page.
    test("the assistant panel opens from the shell and its shortcut on any page", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/content/read/${shuttle}`);
      const panel = page.getByRole("dialog", { name: msg(locale, "assistant.title") });
      await page.keyboard.press("Control+Slash");
      await expect(panel).toBeVisible();
      await expect(panel.getByLabel(msg(locale, "assistant.label"), { exact: true })).toBeFocused();
      await page.keyboard.press("Escape");
      await expect(panel).toBeHidden();
      // Opened from the button, closing gives focus back to it.
      const opener = page.locator(".shell-bar").getByRole("button", { name: msg(locale, "shell.ask") });
      await opener.click();
      await expect(panel).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(panel).toBeHidden();
      await expect(opener).toBeFocused();

      await opener.click();
      await expect(panel).toBeVisible();
      await panel.getByLabel(msg(locale, "assistant.label"), { exact: true }).fill(questions[locale]);
      await panel.getByRole("button", { name: msg(locale, "assistant.ask"), exact: true }).click();
      const turn = panel.locator(".assistant-turn").first();
      await expect(turn.getByText(msg(locale, "assistant.generated"))).toBeVisible();
      await expect(turn.locator(".assistant-sources").getByRole("link", { name: /Evening shuttle timetable|مواعيد الحافلة المسائية/ })).toHaveAttribute(
        "href",
        `/content/read/${shuttle}`,
      );
      // The page behind keeps its place: the panel did not navigate.
      await expect(page).toHaveURL(new RegExp(`/content/read/${shuttle}$`));
      await expectAccessible(page, testInfo, `assistant-panel-${locale}`);

      await panel.getByRole("link", { name: msg(locale, "assistant.panel.fullPage"), exact: true }).click();
      await page.waitForURL(/\/assistant\?c=/);
      await expect(page.locator(".assistant-main .assistant-turn")).toHaveCount(1);
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
      // The text tools are modes of the one composer; the result joins the transcript (FR-025).
      const main = page.locator(".assistant-main");
      await expect(page.locator(".assistant-tools")).toHaveCount(0);
      await main.getByLabel(msg(locale, "assistant.mode.label"), { exact: true }).selectOption("rewrite");
      await main.getByLabel(msg(locale, "assistant.tools.text"), { exact: true }).fill("pls send the report by friday thx");
      await main.getByRole("button", { name: msg(locale, "assistant.tools.run"), exact: true }).click();
      await expect(main.getByText(msg(locale, "assistant.generated"))).toBeVisible();
      await expect(main.getByRole("button", { name: msg(locale, "assistant.tools.copy"), exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `assistant-tools-${locale}`);
    });

    // Spec 014 US5: a file brought to a new conversation is read, shown as a chip and cited by the answer.
    test("a file brought to a conversation is cited in its answer", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/assistant`);
      const files = page.locator(".assistant-files");
      await expect(files.getByRole("button", { name: msg(locale, "assistant.files.attach"), exact: true })).toBeVisible();
      await files.locator('input[type="file"]').setInputFiles({
        name: "falcon-notes.txt",
        mimeType: "text/plain",
        buffer: Buffer.from("Project Falcon notes. The Falcon pilot starts in the Riyadh warehouse with forty scanners."),
      });
      await expect(files.getByRole("list", { name: msg(locale, "assistant.files.list") }).getByText("falcon-notes.txt")).toBeVisible();
      await expectAccessible(page, testInfo, `assistant-files-${locale}`);

      await page.getByLabel(msg(locale, "assistant.label"), { exact: true }).fill("Where does the Falcon pilot start?");
      await page.getByRole("button", { name: msg(locale, "assistant.ask"), exact: true }).click();
      await page.waitForURL(/\/assistant\?c=/);
      const turn = page.locator(".assistant-turn").first();
      await expect(turn.locator(".assistant-sources").getByRole("link", { name: "falcon-notes.txt" })).toBeVisible();
      await expect(page.locator(".assistant-files").getByText("falcon-notes.txt")).toBeVisible();
    });

    // Spec 014 US6: a spoken question lands in the question box to edit before it is sent, and an answer is read aloud.
    test("a spoken question is put in the box to edit and an answer is read aloud", async ({ page }, testInfo) => {
      // The browser's microphone and player are stood in for: the recording carries the words the fake model hears.
      await page.addInitScript(() => {
        const spoken = "speech:Where does the Falcon pilot start?";
        Object.defineProperty(navigator, "mediaDevices", {
          configurable: true,
          value: { getUserMedia: async () => ({ getTracks: () => [{ stop() {} }] }) },
        });
        class FakeRecorder {
          static isTypeSupported(type: string) {
            return type.startsWith("audio/webm");
          }
          state = "inactive";
          ondataavailable: ((e: { data: Blob }) => void) | null = null;
          onstop: (() => void) | null = null;
          constructor(public stream: unknown) {}
          start() {
            this.state = "recording";
          }
          stop() {
            this.state = "inactive";
            this.ondataavailable?.({ data: new Blob([spoken], { type: "audio/webm" }) });
            this.onstop?.();
          }
        }
        Object.defineProperty(window, "MediaRecorder", { configurable: true, value: FakeRecorder });
        (window as unknown as { __played: number }).__played = 0;
        HTMLMediaElement.prototype.play = function () {
          (window as unknown as { __played: number }).__played += 1;
          return Promise.resolve();
        };
      });
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/assistant`);
      const mic = page.getByRole("button", { name: msg(locale, "assistant.voice.start"), exact: true });
      await mic.click();
      await page.getByRole("button", { name: msg(locale, "assistant.voice.stop"), exact: true }).click();
      const box = page.getByLabel(msg(locale, "assistant.label"), { exact: true });
      await expect(box).toHaveValue("Where does the Falcon pilot start?");
      await expectAccessible(page, testInfo, `assistant-voice-${locale}`);

      // Nothing is asked until the person sends it.
      await expect(page.locator(".assistant-turn")).toHaveCount(0);
      await box.fill(questions[locale]);
      await page.getByRole("button", { name: msg(locale, "assistant.ask"), exact: true }).click();
      await page.waitForURL(/\/assistant\?c=/);
      const turn = page.locator(".assistant-turn").first();
      await turn.getByRole("button", { name: msg(locale, "assistant.voice.read"), exact: true }).click();
      await expect(turn.getByRole("button", { name: msg(locale, "assistant.voice.stopReading"), exact: true })).toBeVisible();
      expect(await page.evaluate(() => (window as unknown as { __played: number }).__played)).toBe(1);
      // The citations stay on screen while it reads.
      await expect(turn.locator(".assistant-sources")).toBeVisible();
    });
  });
}
