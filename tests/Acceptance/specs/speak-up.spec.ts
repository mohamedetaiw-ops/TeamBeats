import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

async function signedIn(tenant: TestTenant, email: string): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

/**
 * Spec 023 T007 and T008: Speak Up in the browser. The engagement administrator adds a Speak Up channel that allows
 * anonymous posts. Layla posts anonymously after the page tells her plainly that her name is not kept, and gets her
 * private link once. The administrator answers privately from the queue, where the post has no author, and Layla
 * reads the answer through the link, which then stops working. What is stored is covered by SpeakUpTests. In English
 * and Arabic, with axe on every screen. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`speak up in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let community: string;
    let link = "";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const channel = locale === "ar" ? "صوتك" : "Your voice";

    test.beforeAll(async () => {
      tenant = await createTenant(`su-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Workplace safety", ar: "سلامة العمل" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      community = ((await created.json()) as { id: string }).id;
      const layla = await signedIn(tenant, laylaEmail);
      const joined = await layla.post(`/api/v1/engagement/communities/${community}:join`);
      expect(joined.ok(), await joined.text()).toBe(true);
      await layla.dispose();
      await admin.dispose();
    });

    test("the administrator adds a Speak Up channel that allows anonymous posts", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await page.getByLabel(m("engagement.channels.key")).fill("your_voice");
      await page.getByLabel(m("engagement.channels.name", { language: locale === "ar" ? "الإنجليزية" : "English" })).fill("Your voice");
      await page.getByLabel(m("engagement.channels.name", { language: locale === "ar" ? "العربية" : "Arabic" })).fill("صوتك");
      await page.getByLabel(m("engagement.channels.kind")).selectOption("speak_up");
      await page.getByLabel(m("engagement.channels.allowAnonymous")).check();
      await expectAccessible(page, testInfo, `speak-up-channel-${locale}`);
      await page.getByRole("button", { name: m("engagement.channels.add") }).click();
      await expect(page.getByText(m("engagement.channels.added"))).toBeVisible();
    });

    test("a member posts anonymously after being told plainly that their name is not kept", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await page.getByLabel(m("engagement.composer.channel")).selectOption({ label: channel });
      await expect(page.getByText(m("engagement.speakUp.reviewed"))).toBeVisible();
      await page.getByLabel(m("engagement.speakUp.postAnonymously")).check();
      await expect(page.getByText(m("engagement.speakUp.anonymousTitle"))).toBeVisible();
      const editor = page.getByRole("textbox", { name: m("engagement.composer.body") });
      await editor.click();
      await page.keyboard.type("The loading bay door does not close properly");
      await expectAccessible(page, testInfo, `speak-up-compose-${locale}`);
      await page.getByRole("button", { name: m("engagement.composer.submit"), exact: true }).click();
      await expect(page.getByText(m("engagement.speakUp.anonymousSentTitle"))).toBeVisible();
      link = await page.getByLabel(m("engagement.speakUp.replyLink")).inputValue();
      expect(link).toContain("/communities/speak-up/reply#");
      // Nothing appears in the feed until a moderator decides.
      await expect(page.getByRole("list", { name: m("engagement.feed.label") })).toBeHidden();
    });

    test("a moderator answers privately and the author reads it once through their link", async ({ page, browser }, testInfo) => {
      const modContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const mod = await modContext.newPage();
      await signInWithDevForm(mod, tenant.key, tenant.adminEmail, locale);
      await mod.goto(`${hostOrigin(tenant.key)}/communities/moderation`);
      const item = mod.getByRole("list", { name: m("engagement.moderation.queueLabel") }).getByRole("article").first();
      await expect(item.getByRole("heading", { name: m("engagement.moderation.anonymous") })).toBeVisible();
      await expect(item.getByText("The loading bay door does not close properly")).toBeVisible();
      await item.getByRole("button", { name: m("engagement.moderation.answerAction") }).click();
      await item.getByLabel(m("engagement.moderation.answer")).fill("Facilities will repair it this week.");
      await expectAccessible(mod, testInfo, `speak-up-queue-${locale}`);
      await item.getByRole("button", { name: m("engagement.moderation.sendAnswer") }).click();
      await expect(mod.getByText(m("engagement.moderation.answered"))).toBeVisible();
      await modContext.close();

      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(link);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.speakUp.replyTitle"));
      await expectAccessible(page, testInfo, `speak-up-reply-${locale}`);
      await page.getByRole("button", { name: m("engagement.speakUp.replyRead") }).click();
      await expect(page.getByText(m("engagement.speakUp.replyAnsweredTitle"))).toBeVisible();
      await expect(page.getByText("Facilities will repair it this week.")).toBeVisible();

      await page.reload();
      await page.getByRole("button", { name: m("engagement.speakUp.replyRead") }).click();
      await expect(page.getByText(m("engagement.speakUp.replyMissingTitle"))).toBeVisible();
    });
  });
}
