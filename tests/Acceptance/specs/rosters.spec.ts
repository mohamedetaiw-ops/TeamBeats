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

const profileId = async (api: APIRequestContext) => ((await (await api.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;

/** The Monday of next week, as an ISO date: in the future and inside the two weeks My shifts lists. */
function nextWeek(): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() + 7 - ((d.getUTCDay() + 6) % 7));
  return d.toISOString().slice(0, 10);
}

/**
 * Spec 022 R3 T029 (US10): HR adds a shift pattern on the roster page, the admin plans Omar's first day of the week on
 * it and publishes, and Omar sees it under My shifts on the attendance page. In English and Arabic, with axe on
 * every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`shift rosters in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let omarEmail: string;
    const week = nextWeek();
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`ro-${locale}`, locale, "workplace_plus", ["omar"]);
      omarEmail = `omar@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const omar = await signedIn(tenant, omarEmail);
      const set = await admin.put(`/api/v1/people/${await profileId(omar)}/manager`, { data: { managerId: await profileId(admin) } });
      expect(set.ok(), await set.text()).toBe(true);
      await Promise.all([admin.dispose(), omar.dispose()]);
    });

    test("HR adds a shift pattern and the manager plans and publishes the week", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/hr/rosters?week=${week}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("rosters.title"));
      await expectAccessible(page, testInfo, `rosters-empty-${locale}`);

      await page.getByLabel(m("rosters.patterns.nameEn")).fill("Early");
      await page.getByLabel(m("rosters.patterns.nameAr")).fill("صباحية");
      await page.getByLabel(m("rosters.patterns.start")).fill("06:00");
      await page.getByLabel(m("rosters.patterns.end")).fill("14:00");
      await page.getByRole("button", { name: m("rosters.patterns.addButton") }).click();
      await expect(page.getByText(m("rosters.patterns.added"))).toBeVisible();

      // The planner offers the new pattern after a reload.
      await page.reload();
      const cell = page.getByRole("combobox").first();
      await cell.selectOption({ index: 2 });
      await page.getByRole("button", { name: m("rosters.save") }).click();
      await expect(page.getByText(m("rosters.saved"))).toBeVisible();
      await page.getByRole("button", { name: m("rosters.publish") }).click();
      await expect(page.getByText(m("rosters.published"))).toBeVisible();
      await expect(page.getByText(m("rosters.statuses.published"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `rosters-published-${locale}`);
    });

    test("the person sees the shift under My shifts", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, omarEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/attendance`);
      await expect(page.getByRole("heading", { name: m("rosters.mine.title") })).toBeVisible();
      await expect(page.getByText("Early").or(page.getByText("صباحية")).first()).toBeVisible();
      await expectAccessible(page, testInfo, `attendance-shifts-${locale}`);
    });
  });
}
