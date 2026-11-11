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

/** A weekday a few days back, inside the 60 days a correction may reach, as yyyy-mm-dd. */
function pastWeekday(): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - 3);
  while (d.getUTCDay() === 0 || d.getUTCDay() === 6) d.setUTCDate(d.getUTCDate() - 1);
  return d.toISOString().slice(0, 10);
}

const profileId = async (api: APIRequestContext) => ((await (await api.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;

/**
 * Spec 022 T020: attendance in the browser. HR turns web sign in on; Layla signs in and sees her month; HR sets a
 * working hours rule and sees the export; her manager sees her on the team page and opens her days. In English and
 * Arabic, with axe on every screen. Layla then asks to correct a day; her manager approves it from the inbox, and the
 * day shows as corrected.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`attendance in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`at-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const layla = await signedIn(tenant, laylaEmail);
      const [adminId, laylaId] = await Promise.all([profileId(admin), profileId(layla)]);
      expect((await admin.put(`/api/v1/people/${laylaId}/manager`, { data: { managerId: adminId } })).ok()).toBe(true);
      const current = await admin.get("/api/v1/hr/attendance-settings");
      expect(current.ok(), await current.text()).toBe(true);
      const settings = await admin.put("/api/v1/hr/attendance-settings", {
        headers: { "If-Match": current.headers()["etag"] ?? "" },
        data: { webSignIn: true, locationMode: "off", sites: [] },
      });
      expect(settings.ok(), await settings.text()).toBe(true);
      await Promise.all([admin.dispose(), layla.dispose()]);
    });

    test("an employee signs in on the web and sees the month", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/attendance`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("attendance.title"));
      await expect(page.getByText(m("attendance.sign.statusNone"))).toBeVisible();
      await expectAccessible(page, testInfo, `attendance-${locale}`);

      await page.getByRole("button", { name: m("attendance.sign.in"), exact: true }).click();
      await expect(page.getByText(m("attendance.sign.signedIn"), { exact: true })).toBeVisible();
      await expect(page.getByRole("heading", { name: m("attendance.days.title") })).toBeVisible();
      await expect(page.getByRole("table").first()).toBeVisible();
      await expectAccessible(page, testInfo, `attendance-signed-in-${locale}`);
    });

    test("HR sets a working hours rule and the manager sees the team", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/attendance`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("attendance.admin.title"));
      await expect(page.getByLabel(m("attendance.admin.settings.webSignIn"))).toBeChecked();
      await expectAccessible(page, testInfo, `attendance-admin-${locale}`);

      await page.getByLabel(m("attendance.admin.rules.start")).fill("08:00");
      await page.getByLabel(m("attendance.admin.rules.end")).fill("16:00");
      await page.getByRole("button", { name: m("attendance.admin.rules.save") }).click();
      await expect(page.getByText(m("attendance.admin.saved"), { exact: true })).toBeVisible();
      const rules = page.getByRole("table", { name: m("attendance.admin.rules.caption") });
      await expect(rules.getByText(m("attendance.admin.rules.hoursValue", { start: "08:00", end: "16:00" }))).toBeVisible();
      await expect(page.getByRole("link", { name: m("attendance.admin.export.download") })).toHaveAttribute("href", /\/api\/v1\/hr\/attendance\/export\?month=\d{4}-\d{2}/);

      await page.goto(`${hostOrigin(tenant.key)}/attendance/team`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("attendance.team.title"));
      const team = page.getByRole("table", { name: m("attendance.team.caption") });
      await expect(team.getByRole("link")).toHaveCount(1);
      await expectAccessible(page, testInfo, `attendance-team-${locale}`);
      await team.getByRole("link").click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("attendance.person.title"));
      await expect(page.getByText(m("attendance.days.rule", { start: "08:00", end: "16:00", late: "15", early: "15" }))).toBeVisible();
      await expectAccessible(page, testInfo, `attendance-person-${locale}`);
    });

    test("an employee asks to correct a day and the manager approves it from the inbox", async ({ page }, testInfo) => {
      const day = pastWeekday();
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/attendance`);
      const form = page.locator("section").filter({ has: page.getByRole("heading", { name: m("attendance.requests.title") }) });
      await form.getByLabel(m("attendance.requests.date"), { exact: true }).fill(day);
      await form.getByLabel(m("attendance.requests.time"), { exact: true }).first().fill("09:00");
      await form.getByLabel(m("attendance.requests.direction"), { exact: true }).first().selectOption("in");
      await form.getByRole("button", { name: m("attendance.requests.addPunch") }).click();
      await form.getByLabel(m("attendance.requests.time"), { exact: true }).nth(1).fill("17:00");
      await form.getByLabel(m("attendance.requests.direction"), { exact: true }).nth(1).selectOption("out");
      await form.getByLabel(m("attendance.requests.reason"), { exact: true }).fill(locale === "ar" ? "نسيت تسجيل الحضور" : "Forgot to sign in");
      await form.getByRole("button", { name: m("attendance.requests.send") }).click();
      await expect(page.getByText(m("attendance.requests.sent"), { exact: true })).toBeVisible();
      await expect(form.getByText(m("attendance.requests.statuses.pending"))).toBeVisible();
      await expectAccessible(page, testInfo, `attendance-request-${locale}`);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      const inbox = page.getByTestId("task-inbox");
      await inbox.getByRole("link").filter({ hasText: day }).first().click();
      await page.waitForURL(/\/attendance\/requests\/[0-9a-f-]{36}\?task=/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("attendance.detail.title"));
      await expectAccessible(page, testInfo, `attendance-decision-${locale}`);
      await page.getByRole("button", { name: m("services.decision.approve") }).click();
      await expect(page.getByText(m("attendance.detail.decided"), { exact: true })).toBeVisible();
      await expect(page.getByText(m("attendance.requests.statuses.approved")).first()).toBeVisible();

      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/attendance`);
      if (day.slice(0, 7) !== new Date().toISOString().slice(0, 7)) {
        await page.getByRole("button", { name: m("attendance.previous") }).click();
      }
      await expect(page.getByText(m("attendance.flags.corrected")).first()).toBeVisible();
    });
  });
}
