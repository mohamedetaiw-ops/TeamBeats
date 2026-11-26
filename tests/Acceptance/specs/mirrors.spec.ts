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
 * Spec 023 R3 T018 (US10): the engagement administrator opens Mirroring from an open community, sees that Slack and
 * Teams are not connected yet, and saves a Teams channel without the excerpt; a closed community explains that it
 * cannot be mirrored. Delivery to Slack and Teams is covered by MirrorTests. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`mirrors in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let open: string;
    let closed: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`mr-${locale}`, locale, "workplace_plus", []);
      const admin = await signedIn(tenant, tenant.adminEmail);
      for (const [kind, en, ar] of [["open", "Announcements", "الإعلانات"], ["closed", "Board", "المجلس"]] as const) {
        const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en, ar }, kind } });
        expect(created.status(), await created.text()).toBe(201);
        const id = ((await created.json()) as { id: string }).id;
        if (kind === "open") open = id;
        else closed = id;
      }
      await admin.dispose();
    });

    test("an administrator links an open community to a Teams channel", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${open}`);
      await page.getByRole("link", { name: m("engagement.mirrors.link") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.mirrors.pageTitle"));
      await expect(page.getByText(m("engagement.mirrors.slackUnavailable"))).toBeVisible();
      const teams = page.getByRole("region", { name: m("engagement.mirrors.teamsTitle") });
      await teams.getByLabel(m("engagement.mirrors.teamsChannel")).fill("19:a1b2c3d4e5@thread.tacv2");
      await expectAccessible(page, testInfo, `mirrors-${locale}`);
      await teams.getByRole("button", { name: m("engagement.mirrors.save") }).click();
      await expect(teams.getByText(m("engagement.mirrors.saved"))).toBeVisible();
      await expect(teams.getByText(m("engagement.mirrors.on"), { exact: true })).toBeVisible();

      await page.goto(`${hostOrigin(tenant.key)}/communities/${closed}/mirrors`);
      await expect(page.getByText(m("engagement.mirrors.notEligibleTitle"))).toBeVisible();
    });
  });
}
