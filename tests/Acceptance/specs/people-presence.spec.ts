import { request, type Route } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 018 R3: a colleague's Teams presence on their profile, in English and Arabic with axe. The Microsoft sign-in
 * needs HTTPS on the tenant host, so the browser's presence call is answered here with the documented shape; the server
 * side, with the fake Microsoft Graph, is in tests/Integration/MailCalendarPresenceTests.cs.
 */
async function profileIdOf(tenant: TestTenant, email: string): Promise<string> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  const api = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
  const id = ((await (await api.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
  await api.dispose();
  return id;
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`Teams presence in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string) => msg(locale, key);
    let tenant: TestTenant;
    let layla: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`prs-${locale}`, locale, "workplace_plus", ["layla"]);
      layla = await profileIdOf(tenant, `layla@${tenant.key}.example`);
    });

    test("a colleague's profile shows their Teams status in words and hides it when there is none", async ({ page }, testInfo) => {
      let answer: Record<string, unknown> = { state: "ok", availability: "busy", activity: "in_a_call" };
      await page.route("**/api/v1/people/*/presence", (route: Route) => route.fulfill({ json: answer }));
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/${layla}`);
      const status = page.getByTestId("person-presence");
      await expect(status).toContainText(m("people.presence.busy"));
      await expect(status).toContainText(m("people.presence.in_a_call"));
      await expectAccessible(page, testInfo, `people-presence-${locale}`);

      // A tenant without the presence feature: nothing, not an error.
      answer = { state: "turned_off", availability: null, activity: null };
      await page.reload();
      await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
      await expect(page.getByTestId("person-presence")).toHaveCount(0);
    });
  });
}
