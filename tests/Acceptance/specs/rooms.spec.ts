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
 * Spec 023 R3 T017 (US9 scenarios 1 and 2): the administrator opens a discussion room from the community's rooms page;
 * Layla, a member, opens it in her own browser and sees the administrator's message arrive live without reloading,
 * then answers. A member asks for an AI summary of a busy room (US11 scenario 2). The rules behind rooms (moderation, mentions, lost access) are covered by RoomTests. In English and
 * Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`rooms in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let community: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`rm-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Night shift", ar: "المناوبة الليلية" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      community = ((await created.json()) as { id: string }).id;
      const layla = await signedIn(tenant, laylaEmail);
      const joined = await layla.post(`/api/v1/engagement/communities/${community}:join`);
      expect(joined.ok(), await joined.text()).toBe(true);
      await layla.dispose();
      await admin.dispose();
    });

    test("an owner opens a room and messages arrive live for a member", async ({ page, browser }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await page.getByRole("link", { name: m("engagement.rooms.link") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.rooms.pageTitle"));
      await expect(page.getByText(m("engagement.rooms.emptyTitle"))).toBeVisible();
      await page.getByLabel(m("engagement.rooms.name", { language: locale === "ar" ? "الإنجليزية" : "English" })).fill("Handover");
      await expectAccessible(page, testInfo, `rooms-list-${locale}`);
      await page.getByRole("button", { name: m("engagement.rooms.create") }).click();
      await expect(page.getByText(m("engagement.rooms.created"))).toBeVisible();
      await page.getByRole("list", { name: m("engagement.rooms.listLabel") }).getByRole("link", { name: "Handover" }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText("Handover");
      await expect(page.getByText(m("engagement.rooms.live"), { exact: true })).toBeVisible();

      const otherContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const other = await otherContext.newPage();
      await signInWithDevForm(other, tenant.key, laylaEmail, locale);
      await other.goto(page.url());
      await expect(other.getByText(m("engagement.rooms.live"), { exact: true })).toBeVisible();
      await expect(other.getByText(m("engagement.rooms.quietTitle"))).toBeVisible();

      await page.getByLabel(m("engagement.rooms.message"), { exact: true }).fill("Pump two is back online");
      await page.getByRole("button", { name: m("engagement.rooms.send") }).click();
      const list = other.getByRole("list", { name: m("engagement.rooms.messagesLabel") });
      await expect(list.getByText("Pump two is back online")).toBeVisible({ timeout: 10_000 });
      await expectAccessible(other, testInfo, `rooms-chat-${locale}`);

      await other.getByLabel(m("engagement.rooms.message"), { exact: true }).fill("Thanks, noted");
      await other.getByRole("button", { name: m("engagement.rooms.send") }).click();
      await expect(page.getByRole("list", { name: m("engagement.rooms.messagesLabel") }).getByText("Thanks, noted")).toBeVisible({ timeout: 10_000 });
      await otherContext.close();
    });

    test("a member summarizes a busy room with AI", async ({ page }, testInfo) => {
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post(`/api/v1/engagement/communities/${community}/rooms`, { data: { names: { en: "Busy desk", ar: "مكتب مزدحم" } } });
      expect(created.status(), await created.text()).toBe(201);
      const room = ((await created.json()) as { id: string }).id;
      for (let i = 0; i < 20; i++) {
        const sent = await admin.post(`/api/v1/engagement/rooms/${room}/messages`, { data: { body: `Ticket ${i} is waiting on parts` } });
        expect(sent.status(), await sent.text()).toBe(201);
      }
      await admin.dispose();

      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}/rooms/${room}`);
      await expect(page.getByText(m("engagement.rooms.live"), { exact: true })).toBeVisible();
      await page.getByRole("button", { name: m("engagement.summary.ask") }).click();
      await expect(page.getByText(m("engagement.summary.title"), { exact: true })).toBeVisible({ timeout: 15_000 });
      await expect(page.getByText(m("engagement.summary.note"))).toBeVisible();
      await expectAccessible(page, testInfo, `rooms-summary-${locale}`);
    });
  });
}
