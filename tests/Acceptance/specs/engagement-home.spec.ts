import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
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
 * Spec 023 T015 (home cards of spec 008 FR-055): a page holds the recognition, leaderboard, celebrations and awards
 * cards, and the latest discussions and active poll cards of the lunch club. The administrator thanks Layla and Layla shares a birthday this week, so the recognition card shows the thank
 * you and the celebrations card shows Layla; the club's post and open poll show, and each card links to its page. Capabilities and limits of the cards are
 * covered by ExperienceValidationTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`engagement home cards in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let slug: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const topic = locale === "ar" ? "من يريد الغداء يوم الخميس؟" : "Who is coming to lunch on Thursday?";
    const question = locale === "ar" ? "أين نتناول الغداء؟" : "Where shall we eat?";
    const thanks = locale === "ar" ? "شكرًا على مساعدتك في الإغلاق الشهري" : "Thank you for your help with the month-end close";

    test.beforeAll(async () => {
      tenant = await createTenant(`eh-${locale}`, locale, "workplace_plus", ["layla"]);
      const layla = await signedIn(tenant, `layla@${tenant.key}.example`);
      const me = (await (await layla.get("/api/v1/me")).json()) as { id: string };
      const soon = new Date(Date.now() + 2 * 86_400_000);
      const birthday = await layla.put("/api/v1/engagement/celebrations/preferences", {
        data: { birthdayMonth: soon.getUTCMonth() + 1, birthdayDay: soon.getUTCDate(), anniversaryOptOut: false },
      });
      expect(birthday.ok(), await birthday.text()).toBe(true);
      const admin = await signedIn(tenant, tenant.adminEmail);
      const thanked = await admin.post("/api/v1/engagement/recognitions", { data: { recipients: [me.id], message: thanks } });
      expect(thanked.status(), await thanked.text()).toBe(201);
      const club = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Lunch club", ar: "نادي الغداء" }, kind: "open" } });
      expect(club.status(), await club.text()).toBe(201);
      const community = ((await club.json()) as { id: string }).id;
      const post = await admin.post(`/api/v1/engagement/communities/${community}/posts`, {
        data: { body: { blocks: [{ type: "paragraph", children: [{ text: topic }] }] } },
      });
      expect(post.status(), await post.text()).toBe(201);
      const poll = await admin.post(`/api/v1/engagement/communities/${community}/polls`, {
        data: { question, options: ["Falafel corner", "Sushi bar"], multiple: false, anonymous: false, allowChange: false, results: "after_vote", closesAt: new Date(Date.now() + 3 * 86_400_000).toISOString() },
      });
      expect(poll.status(), await poll.text()).toBe(201);
      const api = await tenantApi(tenant);
      const page = await createPage(api, "T01", `eh-${Date.now().toString(36)}`);
      await saveAndPublish(
        api,
        page,
        page.draft.regions.map((r, i) => ({
          ...r,
          placements:
            i > 0
              ? []
              : [
                  { id: "recognition", componentRef: "engagement.recognition@1", props: { top: 3 } },
                  { id: "celebrations", componentRef: "engagement.celebrations@1", props: { top: 4 } },
                  { id: "leaderboard", componentRef: "engagement.leaderboard@1", props: { top: 5 } },
                  { id: "awards", componentRef: "engagement.awards@1", props: { top: 3 } },
                  { id: "discussions", componentRef: "engagement.discussions@1", props: { community, top: 3 } },
                  { id: "poll", componentRef: "engagement.poll@1", props: { community } },
                ],
        })),
      );
      slug = page.slug;
      await Promise.all([layla.dispose(), admin.dispose(), api.dispose()]);
    });

    test("the cards show recognition, celebrations, discussions and the open poll and open their pages", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/p/${slug}`);
      const recognition = page.getByRole("region", { name: m("engagement.home.recognitionTitle") });
      await expect(recognition.getByText(thanks)).toBeVisible();
      const celebrations = page.getByRole("region", { name: m("engagement.home.celebrationsTitle") });
      await expect(celebrations.getByText(/layla/i).first()).toBeVisible();
      await expect(celebrations.getByText(m("engagement.celebrations.kinds.birthday"), { exact: true })).toBeVisible();
      await expect(page.getByRole("region", { name: m("engagement.home.leaderboardTitle") })).toBeVisible();
      const awards = page.getByRole("region", { name: m("engagement.home.awardsTitle") });
      await expect(awards.getByText(m("engagement.home.awardsEmpty"))).toBeVisible();
      const discussions = page.getByRole("region", { name: m("engagement.home.discussionsTitle") });
      await expect(discussions.getByRole("link", { name: topic })).toBeVisible();
      const poll = page.getByRole("region", { name: m("engagement.home.pollTitle") });
      await expect(poll.getByText(question)).toBeVisible();
      await expectAccessible(page, testInfo, `engagement-home-${locale}`);

      await poll.getByRole("link", { name: m("engagement.polls.vote") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.polls.pageTitle"));
      await page.goBack();

      await celebrations.getByRole("link", { name: m("engagement.home.celebrationsAll") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.celebrations.pageTitle"));
    });
  });
}
