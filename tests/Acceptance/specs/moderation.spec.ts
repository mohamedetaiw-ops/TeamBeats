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

const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

/**
 * Spec 023 T005 and T006: moderation in the browser. The engagement administrator adds a word to the keyword filter
 * from the moderation page. Layla's post with that word waits for a moderator and she is told; the administrator
 * approves it from the queue and it shows. A second held post is removed with a reason, which Layla reads on the post.
 * Reports and mutes by people without a role are covered by the integration tests (ModerationTests), since test
 * tenants only have administrators. In English and Arabic, with axe on every screen. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`moderation in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let community: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const square = locale === "ar" ? "الساحة" : "Town square";

    test.beforeAll(async () => {
      tenant = await createTenant(`md-${locale}`, locale, "workplace", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Town square", ar: "الساحة" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      community = ((await created.json()) as { id: string }).id;
      const layla = await signedIn(tenant, laylaEmail);
      const joined = await layla.post(`/api/v1/engagement/communities/${community}:join`);
      expect(joined.ok(), await joined.text()).toBe(true);
      await layla.dispose();
      await admin.dispose();
    });

    test("the administrator adds a word to the keyword filter from the moderation page", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      await page.getByRole("link", { name: m("engagement.moderation.queueLink") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.moderation.pageTitle"));
      await expect(page.getByText(m("engagement.moderation.emptyTitle"))).toBeVisible();
      await page.getByLabel(m("engagement.moderation.keywords")).fill("Bargain\nbargain\n");
      await page.getByRole("button", { name: m("engagement.moderation.saveKeywords") }).click();
      await expect(page.getByText(m("engagement.moderation.keywordsSaved"))).toBeVisible();
      // Saved words come back trimmed, lower case and once each.
      await expect(page.getByLabel(m("engagement.moderation.keywords"))).toHaveValue("bargain");
      await expectAccessible(page, testInfo, `moderation-empty-${locale}`);
    });

    test("a held post waits for a moderator, who approves it from the queue", async ({ page, browser }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(square);
      const editor = page.getByRole("textbox", { name: m("engagement.composer.body") });
      await editor.click();
      await page.keyboard.type("A real bargain on desk lamps");
      await page.getByRole("button", { name: m("engagement.composer.submit"), exact: true }).click();
      await expect(page.locator("form").getByText(m("engagement.moderation.heldText"))).toBeVisible();
      const post = page.getByRole("list", { name: m("engagement.feed.label") }).getByRole("article").first();
      await expect(post.getByText(m("engagement.moderation.heldTitle"))).toBeVisible();
      await expectAccessible(page, testInfo, `moderation-held-${locale}`);

      const modContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const mod = await modContext.newPage();
      await signInWithDevForm(mod, tenant.key, tenant.adminEmail, locale);
      await mod.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await mod.getByRole("link", { name: m("engagement.moderation.queueLink") }).click();
      const item = mod.getByRole("list", { name: m("engagement.moderation.queueLabel") }).getByRole("article").first();
      await expect(item.getByText("A real bargain on desk lamps")).toBeVisible();
      await expect(item.getByText(m("engagement.moderation.keywordHeld"))).toBeVisible();
      await expectAccessible(mod, testInfo, `moderation-queue-${locale}`);
      await item.getByRole("button", { name: m("engagement.moderation.approve") }).click();
      await expect(mod.getByText(m("engagement.moderation.approved"))).toBeVisible();
      await expect(mod.getByText(m("engagement.moderation.emptyTitle"))).toBeVisible();
      await modContext.close();

      await page.reload();
      const shown = page.getByRole("list", { name: m("engagement.feed.label") }).getByRole("article").first();
      await expect(shown.getByText("A real bargain on desk lamps")).toBeVisible();
      await expect(shown.getByText(m("engagement.moderation.heldTitle"))).toBeHidden();
    });

    test("a moderator removes a held post with a reason that the author reads", async ({ page, browser }, testInfo) => {
      const layla = await signedIn(tenant, laylaEmail);
      const created = await layla.post(`/api/v1/engagement/communities/${community}/posts`, { data: { body: body("Another bargain, chairs this time") } });
      expect(created.ok(), await created.text()).toBe(true);
      const postId = ((await created.json()) as { post: { id: string } }).post.id;
      await layla.dispose();

      const modContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const mod = await modContext.newPage();
      await signInWithDevForm(mod, tenant.key, tenant.adminEmail, locale);
      await mod.goto(`${hostOrigin(tenant.key)}/communities/moderation`);
      const item = mod.getByRole("list", { name: m("engagement.moderation.queueLabel") }).getByRole("article").first();
      await item.getByRole("button", { name: m("engagement.moderation.remove") }).click();
      const confirm = item.getByRole("button", { name: m("engagement.moderation.confirmRemove") });
      await expect(confirm).toBeDisabled();
      await item.getByLabel(m("engagement.moderation.removalReason")).fill("Selling is for the marketplace community");
      await expectAccessible(mod, testInfo, `moderation-remove-${locale}`);
      await confirm.click();
      await expect(mod.getByText(m("engagement.moderation.removed"))).toBeVisible();
      await modContext.close();

      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}/posts/${postId}`);
      await expect(page.getByText(m("engagement.moderation.removedTitle"))).toBeVisible();
      await expect(page.getByText("Selling is for the marketplace community")).toBeVisible();
      await expectAccessible(page, testInfo, `moderation-removed-${locale}`);
    });
  });
}
