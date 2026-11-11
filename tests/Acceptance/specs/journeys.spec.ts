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

/**
 * Spec 022 T018: journeys in the browser. HR has an onboarding template and starts Lina's journey; HR sees it on the
 * dashboard next to the templates; Lina opens her step from the inbox and marks it done; HR opens the journey and
 * cancels it with a reason. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`journeys in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let linaEmail: string;
    let journeyId: string;
    const templateName = locale === "ar" ? "التعيين" : "Onboarding";
    const welcome = locale === "ar" ? "اقرأ دليل الترحيب" : "Read the welcome pack";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`jr-${locale}`, locale, "workplace_plus", ["lina"]);
      linaEmail = `lina@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const lina = await signedIn(tenant, linaEmail);
      const linaId = await profileId(lina);
      const template = await admin.post("/api/v1/hr/journey-templates", {
        data: {
          key: "onboarding",
          kind: "onboarding",
          names: { en: "Onboarding", ar: "التعيين" },
          startOn: "manual",
          steps: [
            { key: "welcome", names: { en: "Read the welcome pack", ar: "اقرأ دليل الترحيب" }, offsetDays: 0, assignee: "person" },
            { key: "desk", names: { en: "Prepare the desk", ar: "جهّز المكتب" }, offsetDays: 0, assignee: "permission", permission: "hr.manage", dueWorkingDays: 2 },
            { key: "checkin", names: { en: "30 day check-in", ar: "متابعة بعد 30 يوماً" }, offsetDays: 30, assignee: "manager" },
          ],
        },
      });
      expect(template.status(), await template.text()).toBe(201);
      const started = await admin.post("/api/v1/hr/journeys", { data: { templateKey: "onboarding", userId: linaId, anchorDate: new Date().toISOString().slice(0, 10) } });
      expect(started.status(), await started.text()).toBe(201);
      journeyId = ((await started.json()) as { journey: { id: string } }).journey.id;
      await Promise.all([admin.dispose(), lina.dispose()]);
    });

    test("HR sees the journey on the dashboard next to the templates", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/journeys`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("journeys.admin.title"));
      const dashboard = page.locator("section", { has: page.locator("#journeys-dashboard") });
      await expect(dashboard.locator("table tbody tr")).toHaveCount(1);
      await expect(dashboard.locator("table tbody").getByRole("cell", { name: templateName })).toBeVisible();
      const templates = page.locator("section", { has: page.locator("#journey-templates") });
      await expect(templates.locator(".journeys-item").filter({ hasText: templateName })).toBeVisible();
      await expectAccessible(page, testInfo, `journeys-admin-${locale}`);
    });

    test("the new joiner opens her step from the inbox and marks it done", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, linaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      const inbox = page.getByTestId("task-inbox");
      await expect(inbox.getByRole("link").first()).toBeVisible();
      await inbox.getByRole("link").first().click();
      await page.waitForURL(/\/hr\/journeys\/[0-9a-f-]{36}\?task=/);
      const steps = page.locator("section", { has: page.locator("#journey-steps") });
      await expect(steps.locator("table tbody tr")).toHaveCount(3);
      await expectAccessible(page, testInfo, `journey-person-${locale}`);
      const row = steps.locator("table tbody tr").filter({ hasText: welcome });
      await row.getByRole("button", { name: m("journeys.detail.markDone") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("journeys.detail.stepDone") })).toBeVisible();
      await expect(row.getByText(m("journeys.detail.stepStatuses.done"))).toBeVisible();
    });

    test("HR opens the journey and cancels it with a reason", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/hr/journeys/${journeyId}`);
      const manage = page.locator("section", { has: page.locator("#journey-manage") });
      await expect(manage).toBeVisible();
      await expectAccessible(page, testInfo, `journey-hr-${locale}`);
      await manage.getByLabel(m("journeys.detail.reason")).fill("Start postponed by a quarter");
      await manage.getByRole("button", { name: m("journeys.detail.cancel") }).click();
      await expect(page.getByRole("status").filter({ hasText: m("journeys.detail.cancelled") })).toBeVisible();
      await expect(page.locator("section", { has: page.locator("#journey-summary") }).getByText(m("journeys.detail.statuses.cancelled"))).toBeVisible();
    });
  });
}
