import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const people = ["p1", "p2", "p3", "p4", "p5"];
const comments = ["[fake:positive] Great idea.", "[fake:positive] Works for me.", "[fake:negative] Too early.", "Fine by me.", "[fake:positive] Thanks."];

/**
 * Spec 023 R3 T020 in the browser (US11 scenario 3): five members comment in a community while sentiment is on for the
 * tenant; the owner opens Sentiment from the community page and, once the background run has labelled the comments, sees
 * the weekly shares with no text shown. In English and Arabic, with axe. Names and comments are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`community sentiment in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let community: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`csn-${locale}`, locale, "workplace_plus", people);
      const admin = await tenantApi(tenant);
      const policy = (await (await admin.get("/api/v1/analytics/policy")).json()) as { version: number };
      const on = await admin.put("/api/v1/analytics/policy", { data: { sentiment: true, version: policy.version } });
      expect(on.ok(), await on.text()).toBe(true);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Canteen", ar: "المقصف" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      community = ((await created.json()) as { id: string }).id;
      const post = await admin.post(`/api/v1/engagement/communities/${community}/posts`, {
        data: { body: { blocks: [{ type: "paragraph", children: [{ text: "Lunch starts at noon from Monday" }] }] } },
      });
      expect(post.status(), await post.text()).toBe(201);
      const postId = ((await post.json()) as { post: { id: string } }).post.id;
      for (const [i, person] of people.entries()) {
        const client = await tenantApiAs(tenant, `${person}@${tenant.key}.example`);
        expect((await client.post(`/api/v1/engagement/communities/${community}:join`)).ok()).toBe(true);
        const comment = await client.post(`/api/v1/engagement/posts/${postId}/comments`, { data: { body: comments[i] } });
        expect(comment.status(), await comment.text()).toBe(201);
        await client.dispose();
      }
      await admin.dispose();
    });

    test("the owner reads the community's weekly sentiment", async ({ page }, testInfo) => {
      test.setTimeout(180_000);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await page.getByRole("link", { name: m("engagement.sentiment.link") }).click();
      await page.waitForURL(/\/sentiment$/);
      await expect(page.getByRole("heading", { level: 1, name: m("engagement.sentiment.pageTitle") })).toBeVisible();

      // The background run labels the comments within a few seconds.
      await expect(async () => {
        await page.reload();
        await expect(page.getByRole("heading", { name: m("engagement.sentiment.trend") })).toBeVisible({ timeout: 2_000 });
      }).toPass({ timeout: 120_000, intervals: [5_000] });
      await expect(page.getByText("Too early")).toHaveCount(0);
      await expectAccessible(page, testInfo, `community-sentiment-${locale}`);
    });
  });
}
