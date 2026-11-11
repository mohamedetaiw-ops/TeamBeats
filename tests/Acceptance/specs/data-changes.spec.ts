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
 * Spec 022 T022: employee data changes in the browser. Layla asks HR to change her emergency contact; HR approves it
 * and, without an HR connection, enters it by hand and marks it done, after which the values are gone. HR sees the
 * settings and the change list. In English and Arabic, with axe on every screen. Values are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`data changes in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let changeId = "";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`dc-${locale}`, locale, "workplace_plus", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
    });

    test("an employee asks to change their emergency contact", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/my-details`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("data_changes.title"));
      await expectAccessible(page, testInfo, `my-details-${locale}`);

      await page.getByLabel(m("data_changes.group")).selectOption("emergency_contact");
      await page.getByLabel(m("data_changes.fields.contact_name")).fill("Omar Example");
      await page.getByLabel(m("data_changes.fields.relationship")).fill("Brother");
      await page.getByLabel(m("data_changes.fields.phone")).fill("+971 50 000 0000");
      await page.getByRole("button", { name: m("data_changes.send") }).click();
      await expect(page.getByText(m("data_changes.sent"))).toBeVisible();
      await expect(page.getByText(m("data_changes.statuses.pending"))).toBeVisible();
      await expectAccessible(page, testInfo, `my-details-sent-${locale}`);

      await page.getByRole("link", { name: m("data_changes.groups.emergency_contact") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("data_changes.detail.title"));
      await expect(page.getByText("Omar Example")).toBeVisible();
      await expect(page.getByRole("button", { name: m("data_changes.detail.withdraw") })).toBeVisible();
      await expectAccessible(page, testInfo, `data-change-detail-${locale}`);
      changeId = page.url().split("/").pop()!;
    });

    test("HR approves it, enters it by hand and marks it done", async ({ page }, testInfo) => {
      const admin = await signedIn(tenant, tenant.adminEmail);
      const tasks = (await (await admin.get("/api/v1/tasks")).json()) as { taskId: string; source: string; link: string }[];
      const task = tasks.find((i) => i.source === "data_change" && i.link.includes(changeId));
      expect(task, "HR has the change in the inbox").toBeTruthy();
      const decided = await admin.post(`/api/v1/tasks/${task!.taskId}/decision`, { data: { outcome: "approve", comment: null } });
      expect(decided.ok(), await decided.text()).toBe(true);
      await admin.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/my-details/requests/${changeId}`);
      await expect(page.getByText(m("data_changes.detail.manual"))).toBeVisible();
      await expectAccessible(page, testInfo, `data-change-approved-${locale}`);
      await page.getByRole("button", { name: m("data_changes.detail.complete") }).click();
      await expect(page.getByText(m("data_changes.detail.completed"))).toBeVisible();
      await expect(page.getByText(m("data_changes.detail.cleared"))).toBeVisible();
      await expect(page.getByText("Omar Example")).toHaveCount(0);
      await expectAccessible(page, testInfo, `data-change-done-${locale}`);
    });

    test("HR sees the settings and the changes", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/data-changes`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("data_changes.admin.title"));
      await expect(page.locator('input[name="group-bank-document"]')).toBeChecked();
      await expect(page.getByText(m("data_changes.statuses.completed"))).toBeVisible();
      await expectAccessible(page, testInfo, `data-changes-admin-${locale}`);
    });
  });
}
