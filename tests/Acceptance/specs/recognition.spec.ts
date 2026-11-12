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
 * Spec 023 T011 (recognition): the administrator thanks Layla on the recognition wall, naming the value "Teamwork";
 * Layla sees the shout-out on the wall in her own browser with her points, and on her profile. Caps, badges limited
 * to a unit, points switched off and leaderboards are covered by RecognitionTests. In English and Arabic, with axe.
 * Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`recognition in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let laylaId: string;
    const teamwork = locale === "ar" ? "العمل الجماعي" : "Teamwork";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`rc-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const layla = await signedIn(tenant, laylaEmail);
      laylaId = ((await (await layla.get("/api/v1/me")).json()) as { id: string }).id;
      const saved = await admin.put("/api/v1/engagement/settings/recognition", {
        data: { values: [{ key: "teamwork", names: { en: "Teamwork", ar: "العمل الجماعي" } }], badges: [] },
      });
      expect(saved.ok(), await saved.text()).toBe(true);
      await layla.dispose();
      await admin.dispose();
    });

    test("a colleague is thanked on the wall and sees it with their points and on their profile", async ({ page, browser }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      await page.getByRole("link", { name: m("engagement.recognition.link") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.recognition.pageTitle"));

      const give = page.getByRole("region", { name: m("engagement.recognition.giveTitle") });
      await give.getByLabel(m("services.person.search")).fill("layla");
      await give.getByRole("button", { name: m("services.person.find") }).click();
      await give.getByRole("list", { name: m("services.person.results") }).getByRole("button").first().click();
      await give.getByLabel(m("engagement.recognition.message")).fill("Thank you for the help with month-end closing");
      await give.getByLabel(m("engagement.recognition.value")).selectOption({ label: teamwork });
      await expectAccessible(page, testInfo, `recognition-give-${locale}`);
      await give.getByRole("button", { name: m("engagement.recognition.give") }).click();
      await expect(give.getByText(m("engagement.recognition.given"))).toBeVisible();
      const wall = page.getByRole("list", { name: m("engagement.recognition.wallLabel") });
      await expect(wall.getByText("Thank you for the help with month-end closing")).toBeVisible();

      const otherContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const other = await otherContext.newPage();
      await signInWithDevForm(other, tenant.key, laylaEmail, locale);
      await other.goto(`${hostOrigin(tenant.key)}/communities/recognition`);
      const card = other.getByRole("list", { name: m("engagement.recognition.wallLabel") }).getByRole("article").first();
      await expect(card.getByText("Thank you for the help with month-end closing")).toBeVisible();
      await expect(card.getByText(teamwork)).toBeVisible();
      await expect(other.getByRole("region", { name: m("engagement.recognition.pointsTitle") })).toContainText(m("engagement.recognition.pointsToday", { points: "10", cap: "50" }));
      await expectAccessible(other, testInfo, `recognition-wall-${locale}`);

      await other.goto(`${hostOrigin(tenant.key)}/people/${laylaId}`);
      await expect(other.getByRole("region", { name: m("people.profile.recognition") }).getByText("Thank you for the help with month-end closing")).toBeVisible();
      await otherContext.close();
    });
  });
}
