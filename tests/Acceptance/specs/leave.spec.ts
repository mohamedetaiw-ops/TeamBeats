import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/** A Monday at least a week ahead whose Friday falls in the same year, as yyyy-mm-dd. */
function nextWeek(): { monday: string; friday: string; year: number } {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() + 7);
  while (d.getUTCDay() !== 1) d.setUTCDate(d.getUTCDate() + 1);
  const end = () => new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + 4));
  while (end().getUTCFullYear() !== d.getUTCFullYear()) d.setUTCDate(d.getUTCDate() + 7);
  return { monday: d.toISOString().slice(0, 10), friday: end().toISOString().slice(0, 10), year: d.getUTCFullYear() };
}

async function signedIn(tenant: TestTenant, email: string): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

const profileId = async (api: APIRequestContext) => ((await (await api.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;

/**
 * Spec 022 T014: leave in the browser. HR sets up annual leave and a balance; Layla requests a week, sees the working
 * days, her balance after and that Omar is away too, and sends it; her manager opens the task from the inbox and
 * approves it; Layla then sees it approved. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`leave in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    const week = nextWeek();
    const typeName = locale === "ar" ? "إجازة سنوية" : "Annual leave";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`lv-${locale}`, locale, "workplace", ["layla", "omar"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const layla = await signedIn(tenant, laylaEmail);
      const omar = await signedIn(tenant, `omar@${tenant.key}.example`);
      const [adminId, laylaId, omarId] = await Promise.all([profileId(admin), profileId(layla), profileId(omar)]);
      for (const id of [laylaId, omarId]) {
        expect((await admin.put(`/api/v1/people/${id}/manager`, { data: { managerId: adminId } })).ok()).toBe(true);
      }
      const type = await admin.put("/api/v1/hr/leave/types/annual", { data: { names: { en: "Annual leave", ar: "إجازة سنوية" }, sensitive: false } });
      expect(type.ok(), await type.text()).toBe(true);
      const policy = await admin.post("/api/v1/hr/leave/policies", { data: { typeKey: "annual", orgUnitId: null, bands: [{ days: 21 }], accrual: "yearly" } });
      expect(policy.status(), await policy.text()).toBe(201);
      for (const id of [laylaId, omarId]) {
        const adjust = await admin.post("/api/v1/hr/leave/adjustments", { data: { userId: id, typeKey: "annual", leaveYear: week.year, delta: 15, note: "Opening balance" } });
        expect(adjust.status(), await adjust.text()).toBe(201);
      }
      // Omar is away the same week; his manager approved it already.
      const omarLeave = await omar.post("/api/v1/hr/leave/requests", { data: { typeKey: "annual", start: week.monday, end: week.friday } });
      expect(omarLeave.status(), await omarLeave.text()).toBe(201);
      const tasks = (await (await admin.get("/api/v1/tasks")).json()) as { taskId: string }[];
      expect(tasks).toHaveLength(1);
      expect((await admin.post(`/api/v1/tasks/${tasks[0]!.taskId}/decision`, { data: { outcome: "approve", comment: null } })).ok()).toBe(true);
      await Promise.all([admin.dispose(), layla.dispose(), omar.dispose()]);
    });

    test("an employee sees the days, the balance after and who else is away, and sends the request", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/leave`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("leave.title"));
      await expect(page.locator(".leave-balance").filter({ hasText: typeName }).first()).toBeVisible();
      await expectAccessible(page, testInfo, `leave-${locale}`);

      const form = page.locator("form.leave-form");
      await form.getByLabel(m("leave.form.start")).fill(week.monday);
      await form.getByLabel(m("leave.form.end")).fill(week.friday);
      const preview = page.getByRole("region", { name: m("leave.form.previewLabel") });
      await expect(preview.locator(".leave-overlap li")).toHaveCount(1);
      await expect(preview.locator(".leave-years li")).toHaveCount(1);
      await expectAccessible(page, testInfo, `leave-preview-${locale}`);

      await form.getByRole("button", { name: m("leave.form.submit") }).click();
      await expect(page.getByRole("status").filter({ hasText: /LV-\d{6}/ })).toBeVisible();
      const mine = page.locator("section", { has: page.locator("#leave-mine") });
      await expect(mine.getByText(m("leave.status.pending"))).toBeVisible();
    });

    test("the manager opens the task from the inbox and approves it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      const inbox = page.getByTestId("task-inbox");
      await expect(inbox.getByRole("link").first()).toBeVisible();
      await expectAccessible(page, testInfo, `leave-inbox-${locale}`);
      await inbox.getByRole("link").first().click();
      await page.waitForURL(/\/leave\/requests\/[0-9a-f-]{36}\?task=/);
      await expect(page.getByRole("heading", { level: 1 })).toContainText("LV-");
      await expectAccessible(page, testInfo, `leave-decision-${locale}`);
      await page.getByRole("button", { name: m("services.decision.approve") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("leave.detail.decided.approve") })).toBeVisible();
      await expect(page.getByText(m("leave.status.approved")).first()).toBeVisible();
    });

    test("the employee sees the leave approved and the team view shows it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/leave`);
      const mine = page.locator("section", { has: page.locator("#leave-mine") });
      await expect(mine.getByText(m("leave.status.approved"))).toBeVisible();
      await page.goto(`${hostOrigin(tenant.key)}/leave/team`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("leave.team.title"));
      await page.getByLabel(m("leave.team.from")).fill(week.monday);
      await expect(page.locator(".leave-grid td[data-status='approved']").first()).toBeVisible();
      await expectAccessible(page, testInfo, `leave-team-${locale}`);
    });
  });
}
