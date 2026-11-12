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
 * Spec 023 T013 (celebrations): Layla shares her birthday as a day and a month; the administrator sees it today on
 * the celebrations page, sends a greeting, and shares their own birthday. Opt-in, anniversaries, joiners, the unit
 * filter and once-only greetings are covered by CelebrationTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`celebrations in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`ce-${locale}`, locale, "workplace_plus", ["layla"]);
      const layla = await signedIn(tenant, `layla@${tenant.key}.example`);
      // Today in the tenant's time zone (Asia/Dubai), as the service counts it: after 20:00 UTC it is already tomorrow there.
      const parts = new Intl.DateTimeFormat("en-US", { timeZone: "Asia/Dubai", month: "numeric", day: "numeric" }).formatToParts(new Date());
      const part = (type: string) => Number(parts.find((p) => p.type === type)!.value);
      const [month, day] = [part("month"), part("day")];
      const saved = await layla.put("/api/v1/engagement/celebrations/preferences", {
        data: { birthdayMonth: month, birthdayDay: day, anniversaryOptOut: false },
      });
      expect(saved.ok(), await saved.text()).toBe(true);
      await layla.dispose();
    });

    test("a shared birthday shows today, a greeting is sent and a birthday is shared", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities`);
      await page.getByRole("link", { name: m("engagement.celebrations.link") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.celebrations.pageTitle"));

      const today = page.getByRole("list", { name: m("engagement.celebrations.todayTitle") });
      const entry = today.getByRole("article").filter({ hasText: /layla/i });
      await expect(entry.getByText(m("engagement.celebrations.kinds.birthday"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `celebrations-${locale}`);
      await entry.getByRole("button", { name: m("engagement.celebrations.greet.birthday") }).click();
      await expect(entry.getByText(m("engagement.celebrations.sent"))).toBeVisible();

      const mine = page.getByRole("region", { name: m("engagement.celebrations.preferencesTitle") });
      await mine.getByLabel(m("engagement.celebrations.birthdayMonth")).selectOption({ index: 3 });
      await mine.getByLabel(m("engagement.celebrations.birthdayDay")).selectOption("14");
      await mine.getByRole("button", { name: m("engagement.celebrations.savePreferences") }).click();
      await expect(mine.getByText(m("engagement.celebrations.saved"))).toBeVisible();
    });
  });
}
