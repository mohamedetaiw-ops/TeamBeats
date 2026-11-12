import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
import { hostOrigin, stack } from "../support/stack";

const day = 86_400_000;
const iso = (ms: number) => new Date(ms).toISOString().slice(0, 10);
/** Today in Asia/Dubai, the tenant zone. */
const dubaiToday = () => iso(Date.now() + 4 * 3_600_000);

/** A Monday at least a week ahead whose Friday falls in the same year. */
function nextMonday(): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() + 7);
  while (d.getUTCDay() !== 1) d.setUTCDate(d.getUTCDate() + 1);
  while (new Date(d.getTime() + 4 * day).getUTCFullYear() !== d.getUTCFullYear()) d.setUTCDate(d.getUTCDate() + 7);
  return iso(d.getTime());
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
 * Spec 037 (unified calendar) in the browser: the light home card opens the workspace; agenda, week, month with
 * ranges and a day panel (a bottom sheet on a phone), RSVP, layer chips, team availability for the manager only, and
 * the manage page. In English and Arabic, with axe on every screen. Data is fictional and set up through the API.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`unified calendar in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US", timezoneId: "Asia/Dubai" });
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    let tenant: TestTenant;
    let slug: string;
    const today = dubaiToday();
    const tomorrow = iso(Date.parse(today) + day);
    const monday = nextMonday();
    const town = locale === "ar" ? "اللقاء المفتوح" : "Town hall";
    const fair = locale === "ar" ? "معرض الكتاب" : "Book fair";

    test.beforeAll(async () => {
      tenant = await createTenant(`cal-${locale}`, locale, "workplace", ["layla", "omar"]);
      const admin = await signedIn(tenant, tenant.adminEmail);
      const omar = await signedIn(tenant, `omar@${tenant.key}.example`);
      const layla = await signedIn(tenant, `layla@${tenant.key}.example`);
      const [adminId, laylaId, omarId] = await Promise.all([profileId(admin), profileId(layla), profileId(omar)]);
      for (const id of [laylaId, omarId]) {
        expect((await admin.put(`/api/v1/people/${id}/manager`, { data: { managerId: adminId } })).ok()).toBe(true);
      }
      expect((await admin.put("/api/v1/hr/leave/types/annual", { data: { names: { en: "Annual leave", ar: "إجازة سنوية" }, sensitive: false } })).ok()).toBe(true);
      expect((await admin.post("/api/v1/hr/leave/policies", { data: { typeKey: "annual", orgUnitId: null, bands: [{ days: 21 }], accrual: "yearly" } })).status()).toBe(201);
      const year = Number(monday.slice(0, 4));
      expect((await admin.post("/api/v1/hr/leave/adjustments", { data: { userId: omarId, typeKey: "annual", leaveYear: year, delta: 15, note: "Opening balance" } })).status()).toBe(201);
      const leave = await omar.post("/api/v1/hr/leave/requests", { data: { typeKey: "annual", start: monday, end: iso(Date.parse(monday) + 2 * day) } });
      expect(leave.status(), await leave.text()).toBe(201);
      const tasks = (await (await admin.get("/api/v1/tasks")).json()) as { taskId: string }[];
      expect((await admin.post(`/api/v1/tasks/${tasks[0]!.taskId}/decision`, { data: { outcome: "approve", comment: null } })).ok()).toBe(true);

      const event = await admin.post("/api/v1/calendar/admin-events", {
        data: {
          titles: { en: "Town hall", ar: "اللقاء المفتوح" }, kind: "company", allDay: false,
          start: `${tomorrow}T13:00:00+04:00`, end: `${tomorrow}T14:00:00+04:00`, timeZone: "Asia/Dubai", location: "Main hall", rsvp: true,
        },
      });
      expect(event.status(), await event.text()).toBe(201);
      const allDay = await admin.post("/api/v1/calendar/admin-events", {
        data: { titles: { en: "Book fair", ar: "معرض الكتاب" }, kind: "important", allDay: true, startDate: monday, endDate: iso(Date.parse(monday) + 3 * day), timeZone: "Asia/Dubai", rsvp: false },
      });
      expect(allDay.status(), await allDay.text()).toBe(201);

      const api = await tenantApi(tenant);
      const page = await createPage(api, "T01", `cal-${Date.now().toString(36)}`);
      await saveAndPublish(api, page, page.draft.regions.map((r, i) => ({ ...r, placements: i > 0 ? [] : [{ id: "calendar", componentRef: "calendar.overview@1", props: { items: 3 } }] })));
      slug = page.slug;
      await Promise.all([admin.dispose(), omar.dispose(), layla.dispose(), api.dispose()]);
    });

    test("the home card shows the week and opens the workspace", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/p/${slug}`);
      const card = page.getByRole("region", { name: m("calendar.title") });
      await expect(card.getByRole("list", { name: m("calendar.weekLabel") }).getByRole("listitem")).toHaveCount(7);
      await expect(card.locator('[aria-current="date"]')).toHaveCount(1);
      await expectAccessible(page, testInfo, `calendar-card-${locale}`);
      await card.getByRole("link", { name: m("calendar.views.month") }).click();
      await expect(page).toHaveURL(/\/calendar\?view=month/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("calendar.title"));
    });

    test("the agenda lists the event, opens its details and takes an RSVP", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/calendar?view=agenda&date=${today}`);
      const row = page.locator(".ui-cal-row").filter({ hasText: town });
      await expect(row).toHaveCount(1);
      await row.getByRole("button", { expanded: false }).click();
      await expect(row.getByText(m("calendar.rsvp.question")).first()).toBeVisible();
      await row.getByRole("button", { name: m("calendar.rsvp.going"), exact: true }).click();
      await expect(row.getByRole("button", { name: m("calendar.rsvp.going"), exact: true })).toHaveAttribute("aria-pressed", "true");
      await expectAccessible(page, testInfo, `calendar-agenda-${locale}`);

      // A non-manager is offered no team view.
      await expect(page.getByRole("radio", { name: m("calendar.views.team") })).toHaveCount(0);
      // Turning the company layer off hides the event; turning it on brings it back.
      const chip = page.getByRole("button", { name: m("calendar.layers.company"), exact: true });
      await chip.click();
      await expect(chip).toHaveAttribute("aria-pressed", "false");
      await expect(page.locator(".ui-cal-row").filter({ hasText: town })).toHaveCount(0);
      await chip.click();
      await expect(page.locator(".ui-cal-row").filter({ hasText: town })).toHaveCount(1);
    });

    test("the month draws ranges and opens the day panel, as a bottom sheet on a phone", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto(`${hostOrigin(tenant.key)}/calendar?view=month&date=${monday}`);
      await expect(page.locator(".ui-cal-bar").filter({ hasText: fair }).first()).toBeVisible();
      await page.locator(".ui-cal-month-day").filter({ hasText: new RegExp(`^${Number(monday.slice(8))}`) }).first().click();
      await expect(page.getByRole("complementary")).toBeVisible();
      await expectAccessible(page, testInfo, `calendar-month-${locale}`);

      // On a phone the open day becomes a bottom sheet; Escape closes it and choosing a day opens it again.
      await page.setViewportSize({ width: 390, height: 844 });
      const sheet = page.getByRole("dialog");
      await expect(sheet).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(sheet).toBeHidden();
      await page.locator(".ui-cal-month-day").filter({ hasText: new RegExp(`^${Number(monday.slice(8))}`) }).first().click();
      await expect(sheet).toBeVisible();
      await expect(sheet.locator(".ui-cal-row").filter({ hasText: fair })).toHaveCount(1);
      await expectAccessible(page, testInfo, `calendar-sheet-${locale}`);
      await page.keyboard.press("Escape");
      await expect(sheet).toBeHidden();
    });

    test("a manager sees team availability with the threshold and upcoming leave", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/calendar?view=team&date=${monday}`);
      await expect(page.getByRole("table", { name: m("calendar.team.heatTitle", { threshold: "70" }) })).toBeVisible();
      await expect(page.getByRole("img", { name: m("calendar.team.underThreshold") }).first()).toBeVisible();
      await expect(page.locator(".cal-people li").filter({ hasText: /omar/i })).toHaveCount(1);
      await expectAccessible(page, testInfo, `calendar-team-${locale}`);
    });

    test("the manage page lists events and creates one", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/calendar/manage`);
      await expect(page.locator(".cal-manage-list li").filter({ hasText: town })).toHaveCount(1);
      await expectAccessible(page, testInfo, `calendar-manage-${locale}`);
      await page.getByRole("button", { name: m("calendar.manage.create") }).click();
      const dialog = page.getByRole("dialog");
      await dialog.getByLabel(m("calendar.manage.titleEn")).fill("Quiz night");
      await dialog.getByLabel(m("calendar.manage.timeZone")).fill("Asia/Dubai");
      await dialog.getByRole("button", { name: m("calendar.manage.save") }).click();
      await expect(dialog).toBeHidden();
      await expect(page.locator(".cal-manage-list li").filter({ hasText: "Quiz night" })).toHaveCount(1);
    });
  });
}
