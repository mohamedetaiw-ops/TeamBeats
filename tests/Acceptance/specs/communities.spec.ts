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
 * Spec 023 T003 and T004: communities in the browser. The engagement administrator starts an open community from the
 * directory and an approval one through the API. Layla joins the open one, posts with a hashtag, reacts and comments,
 * then finds the post on the hashtag page; she asks to join the approval one and the owner accepts her on the members
 * page. Test tenants only have administrators, so who may do what without a role is covered by the integration tests
 * (CommunityTests). In English and Arabic, with axe on every screen. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`communities in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const club = locale === "ar" ? "نادي التصوير" : "Photography club";
    const runners = locale === "ar" ? "العداؤون" : "Runners";
    const tag = locale === "ar" ? "الساعة_الذهبية" : "golden_hour";

    test.beforeAll(async () => {
      tenant = await createTenant(`cm-${locale}`, locale, "workplace", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Runners", ar: "العداؤون" }, kind: "approval" } });
      expect(created.status(), await created.text()).toBe(201);
      // Layla signs in once so she exists as a person in the tenant.
      await (await signedIn(tenant, laylaEmail)).dispose();
      await admin.dispose();
    });

    test("the administrator starts an open community from the directory", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.pageTitle"));
      await expectAccessible(page, testInfo, `communities-${locale}`);

      await page.getByLabel(m("engagement.create.name", { language: locale === "ar" ? "الإنجليزية" : "English" })).fill("Photography club");
      await page.getByLabel(m("engagement.create.name", { language: locale === "ar" ? "العربية" : "Arabic" })).fill("نادي التصوير");
      await page.getByLabel(m("engagement.create.kind")).selectOption("open");
      await page.getByRole("button", { name: m("engagement.create.submit") }).click();
      await expect(page.getByText(m("engagement.create.created", { name: club }))).toBeVisible();
      await expect(page.getByRole("link", { name: club })).toBeVisible();
      await expectAccessible(page, testInfo, `communities-created-${locale}`);
    });

    test("a colleague joins, posts with a hashtag, reacts, comments and finds the post by its hashtag", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      const card = page.getByRole("article").filter({ has: page.getByRole("heading", { name: club }) });
      await card.getByRole("button", { name: m("engagement.community.join") }).click();
      await expect(page.getByText(m("engagement.directory.joined", { name: club }))).toBeVisible();
      await expect(card.getByText(m("engagement.directory.member"), { exact: true })).toBeVisible();

      await page.getByRole("link", { name: club }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(club);
      await expectAccessible(page, testInfo, `community-${locale}`);

      const body = page.getByRole("textbox", { name: m("engagement.composer.body") });
      await body.click();
      await page.keyboard.type(`Golden hour walk on Thursday #${tag}`);
      await page.getByRole("button", { name: m("engagement.composer.submit"), exact: true }).click();
      await expect(page.getByText(m("engagement.composer.posted"))).toBeVisible();
      const post = page.getByRole("list", { name: m("engagement.feed.label") }).getByRole("article").first();
      await expect(post.getByText("Golden hour walk on Thursday", { exact: false })).toBeVisible();

      const like = post.getByRole("button", { name: m("engagement.reactions.count", { reaction: m("engagement.reactions.like"), count: "0" }) });
      await like.click();
      await expect(post.getByRole("button", { name: m("engagement.reactions.count", { reaction: m("engagement.reactions.like"), count: "1" }) })).toHaveAttribute("aria-pressed", "true");

      // The comments toggle is the post's only disclosure button; its label counts the comments.
      await post.getByRole("button", { expanded: false }).click();
      await post.getByLabel(m("engagement.comments.add")).fill("Count me in");
      await post.getByRole("button", { name: m("engagement.comments.send") }).click();
      await expect(post.getByText("Count me in")).toBeVisible();
      await expectAccessible(page, testInfo, `community-post-${locale}`);

      await post.getByRole("link", { name: m("engagement.hashtag.title", { tag }) }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.hashtag.title", { tag }));
      await expect(page.getByText("Golden hour walk on Thursday", { exact: false })).toBeVisible();
      await expectAccessible(page, testInfo, `community-hashtag-${locale}`);
    });

    test("an approval community takes a request that the owner accepts on the members page", async ({ page, browser }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      const card = page.getByRole("article").filter({ has: page.getByRole("heading", { name: runners }) });
      await card.getByRole("button", { name: m("engagement.community.askToJoin") }).click();
      await expect(page.getByText(m("engagement.directory.requested", { name: runners }))).toBeVisible();
      await expect(card.getByText(m("engagement.directory.pending"))).toBeVisible();

      const ownerContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const owner = await ownerContext.newPage();
      await signInWithDevForm(owner, tenant.key, tenant.adminEmail, locale);
      await owner.goto(`${hostOrigin(tenant.key)}/communities`);
      await owner.getByRole("link", { name: runners }).click();
      await owner.getByRole("link", { name: m("engagement.community.membersLink") }).click();
      await expect(owner.getByRole("heading", { level: 1 })).toHaveText(m("engagement.members.pageTitle"));
      await expect(owner.getByText(m("engagement.members.pending"))).toBeVisible();
      await expectAccessible(owner, testInfo, `community-members-${locale}`);
      await owner.getByRole("button", { name: m("engagement.members.accept") }).click();
      await expect(owner.getByText(m("engagement.members.pending"))).toBeHidden();
      await ownerContext.close();

      await page.reload();
      await expect(page.getByRole("article").filter({ has: page.getByRole("heading", { name: runners }) }).getByText(m("engagement.directory.member"), { exact: true })).toBeVisible();
    });
  });
}
