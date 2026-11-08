import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { request } from "@playwright/test";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 016 T015: the services journey in the browser. An owner builds a form service with one approval step, previews
 * and publishes it; a colleague finds it in the catalogue, files a request and follows it in My requests; the approver
 * sees the header badge, opens the task from the inbox and approves it; the requester then sees it approved in
 * Tracking, and the manager finds it in Team requests once the reporting line names them (T016). In English and Arabic,
 * with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`services in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    const serviceName = locale === "ar" ? "بطاقات العمل" : "Business cards";
    const fieldLabel = locale === "ar" ? "عدد العلب" : "Number of boxes";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`svc-${locale}`, locale, "workplace", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
    });

    test("an owner builds, previews and publishes a form service with an approval step", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/services`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("services.admin.title"));
      const create = page.getByRole("form", { name: m("services.admin.createTitle") });
      await create.getByLabel(m("services.admin.nameEn")).fill("Business cards");
      await create.getByLabel(m("services.admin.nameAr")).fill("بطاقات العمل");
      await expectAccessible(page, testInfo, `services-admin-${locale}`);
      await create.getByRole("button", { name: m("services.admin.create") }).click();
      await page.waitForURL(/\/admin\/services\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(serviceName);

      const form = page.getByRole("form", { name: m("services.admin.formTitle") });
      await form.getByRole("button", { name: m("services.admin.addField") }).click();
      const field = form.getByTestId("builder-field").first();
      await field.getByLabel(m("services.admin.fieldKey")).fill("boxes");
      await field.getByLabel(m("services.admin.fieldType")).selectOption("number");
      await field.getByLabel(m("services.admin.inEnglish", { label: m("services.admin.fieldLabel") })).fill("Number of boxes");
      await field.getByLabel(m("services.admin.inArabic", { label: m("services.admin.fieldLabel") })).fill("عدد العلب");
      await field.getByLabel(m("services.admin.required")).check();
      await field.getByLabel(m("services.admin.max")).fill("3");
      await form.getByLabel(m("services.admin.approvalKind")).selectOption("permission_at_unit");
      await form.getByLabel(m("services.admin.inEnglish", { label: m("services.admin.stepName") })).fill("Logistics approval");
      await form.getByLabel(m("services.admin.inArabic", { label: m("services.admin.stepName") })).fill("موافقة اللوجستيات");

      await form.getByRole("button", { name: m("services.admin.preview") }).click();
      const preview = page.getByTestId("service-preview");
      await expect(preview.getByLabel(fieldLabel)).toBeVisible();
      await expect(preview.getByText(m("services.form.previewOnly"))).toBeVisible();
      await expectAccessible(page, testInfo, `services-builder-${locale}`);

      await form.getByRole("button", { name: m("services.admin.publish") }).click();
      await expect(form.getByText(m("services.admin.published"))).toBeVisible();
      await expect(page.getByTestId("service-state")).toHaveText(m("services.admin.state.published"));
    });

    test("a colleague finds the service, files a request and follows it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/services`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("services.catalogue.title"));
      const search = page.getByRole("search");
      await search.getByLabel(m("services.catalogue.search")).fill(serviceName.split(" ")[0]!);
      await search.getByRole("button", { name: m("services.catalogue.submit") }).click();
      const results = page.getByTestId("services-results");
      await expect(results.getByRole("link", { name: serviceName })).toBeVisible();
      await expectAccessible(page, testInfo, `services-catalogue-${locale}`);

      await results.getByRole("link", { name: serviceName }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(serviceName);
      await page.getByLabel(fieldLabel).fill("9");
      await page.getByRole("button", { name: m("services.form.submit") }).click();
      await expect(page.getByLabel(fieldLabel)).toHaveAttribute("aria-invalid", "true");
      await expectAccessible(page, testInfo, `services-form-${locale}`);

      await page.getByLabel(fieldLabel).fill("2");
      await page.getByRole("button", { name: m("services.form.submit") }).click();
      await page.waitForURL(/\/requests\/[0-9a-f-]{36}$/);
      await expect(page.getByTestId("request-status")).toHaveText(m("services.status.in_approval"));
      await expect(page.getByTestId("request-tracking")).toContainText(m("services.request.event.submitted"));
      await expectAccessible(page, testInfo, `services-request-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/requests`);
      await expect(page.getByTestId("my-requests").getByRole("link", { name: serviceName })).toBeVisible();
      await expectAccessible(page, testInfo, `services-my-requests-${locale}`);
    });

    test("the approver sees the badge, opens the task from the inbox and approves it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await expect(page.getByTestId("task-badge")).toContainText("1");
      await page.getByTestId("task-badge").click();
      await page.waitForURL(`${hostOrigin(tenant.key)}/tasks`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("services.inbox.pageTitle"));
      const inbox = page.getByTestId("task-inbox");
      await expect(inbox.getByText(locale === "ar" ? "موافقة اللوجستيات" : "Logistics approval")).toBeVisible();
      await expectAccessible(page, testInfo, `services-inbox-${locale}`);

      await inbox.getByRole("link").first().click();
      await page.waitForURL(/\/requests\/[0-9a-f-]{36}\?task=/);
      await page.getByRole("button", { name: m("services.decision.reject") }).click();
      await expect(page.getByText(m("services.decision.reasonRequired"))).toBeVisible();
      await expectAccessible(page, testInfo, `services-decision-${locale}`);
      await page.getByLabel(m("services.decision.comment")).fill(locale === "ar" ? "تمت الموافقة" : "Approved for the event");
      await page.getByRole("button", { name: m("services.decision.approve") }).click();
      await page.waitForURL(`${hostOrigin(tenant.key)}/tasks`);
      await expect(page.getByText(m("services.inbox.empty"))).toBeVisible();
    });

    test("the requester sees the request approved in Tracking", async ({ page }) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/requests?status=approved`);
      await page.getByTestId("my-requests").getByRole("link", { name: serviceName }).click();
      await expect(page.getByTestId("request-status")).toHaveText(m("services.status.approved"));
      await expect(page.getByTestId("request-tracking")).toContainText(m("services.request.event.approved"));
    });

    test("the manager finds the request in Team requests", async ({ page }, testInfo) => {
      const host = `${tenant.key}.${stack.baseDomain}`;
      const asLayla = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
      expect((await asLayla.post("/dev/signin", { data: { email: laylaEmail } })).ok()).toBe(true);
      const laylaId = ((await (await asLayla.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
      await asLayla.dispose();
      const admin = await tenantApi(tenant);
      const adminId = ((await (await admin.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
      expect((await admin.put(`/api/v1/people/${laylaId}/manager`, { data: { managerId: adminId } })).ok()).toBe(true);
      await admin.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/requests/team`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("services.team.title"));
      await expect(page.getByTestId("team-requests").getByRole("link", { name: serviceName })).toBeVisible();
      await expectAccessible(page, testInfo, `services-team-${locale}`);
    });
  });
}
