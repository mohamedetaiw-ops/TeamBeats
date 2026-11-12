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
 * Spec 023 T009 (polls): Layla starts a poll on the community's polls page; the administrator votes in their own
 * browser and sees results after voting; Layla closes the poll and it shows as closed with the counts. Anonymous
 * polls and the rules behind them are covered by PollTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`polls in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let community: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`pl-${locale}`, locale, "workplace", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Lunch club", ar: "نادي الغداء" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      community = ((await created.json()) as { id: string }).id;
      const layla = await signedIn(tenant, laylaEmail);
      const joined = await layla.post(`/api/v1/engagement/communities/${community}:join`);
      expect(joined.ok(), await joined.text()).toBe(true);
      await layla.dispose();
      await admin.dispose();
    });

    test("a member starts a poll, a colleague votes and sees results, and the author closes it", async ({ page, browser }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await page.getByRole("link", { name: m("engagement.polls.link") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.polls.pageTitle"));
      await page.getByLabel(m("engagement.polls.question")).fill("Where shall we eat on Thursday?");
      await page.getByLabel(m("engagement.polls.options")).fill("Falafel corner\nSushi bar\nPizza place");
      await expectAccessible(page, testInfo, `polls-create-${locale}`);
      await page.getByRole("button", { name: m("engagement.polls.create") }).click();
      await expect(page.getByText(m("engagement.polls.created"))).toBeVisible();
      const poll = page.getByRole("list", { name: m("engagement.polls.listLabel") }).getByRole("article").first();
      await expect(poll.getByRole("heading", { name: "Where shall we eat on Thursday?" })).toBeVisible();

      const otherContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const other = await otherContext.newPage();
      await signInWithDevForm(other, tenant.key, tenant.adminEmail, locale);
      await other.goto(`${hostOrigin(tenant.key)}/communities/${community}/polls`);
      const card = other.getByRole("list", { name: m("engagement.polls.listLabel") }).getByRole("article").first();
      // The administrator moderates the community, so results already show before voting.
      await card.getByRole("radio", { name: "Sushi bar" }).check();
      await expectAccessible(other, testInfo, `polls-vote-${locale}`);
      await card.getByRole("button", { name: m("engagement.polls.vote"), exact: true }).click();
      await expect(card.getByRole("group", { name: m("engagement.polls.resultsLabel") })).toBeVisible();
      await expect(card.getByRole("progressbar", { name: "Sushi bar" })).toHaveAttribute("value", "1");
      await otherContext.close();

      await page.reload();
      const mine = page.getByRole("list", { name: m("engagement.polls.listLabel") }).getByRole("article").first();
      await mine.getByRole("button", { name: m("engagement.polls.close") }).click();
      await expect(mine.getByText(m("engagement.polls.closed"), { exact: true })).toBeVisible();
      await expect(mine.getByRole("group", { name: m("engagement.polls.resultsLabel") })).toBeVisible();
      await expectAccessible(page, testInfo, `polls-closed-${locale}`);
    });
  });
}
