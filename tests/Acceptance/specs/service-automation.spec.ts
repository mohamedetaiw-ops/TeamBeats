import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 016 US8 and spec 017 T009 in the browser: an owner adds an automatic step that calls a connector action, maps a
 * form field to the action's field and publishes; a colleague files the request and sees the automatic step on it.
 * Retry, skip and cancel of a failed step are covered by the integration suite, which controls the external system.
 * In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`service automation in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const serviceName = locale === "ar" ? "حاسوب جديد" : "New laptop";
    const stepName = locale === "ar" ? "فتح تذكرة" : "Open the ticket";

    test.beforeAll(async () => {
      tenant = await createTenant(`auto-${locale}`, locale, "workplace_plus", ["layla"]);
      const api = await tenantApi(tenant);
      const host = `desk-${locale}.example`;
      expect((await api.post("/api/v1/integrations/allowlist", { data: { host } })).ok()).toBe(true);
      const created = await api.post("/api/v1/integrations/pack-connections", {
        data: { kind: "custom_rest", displayName: "Service desk", settings: { baseUrl: `https://${host}/v1`, auth: { type: "none" } } },
      });
      expect(created.ok()).toBe(true);
      const id = ((await created.json()) as { id: string }).id;
      const action = await api.put(`/api/v1/integrations/pack-connections/${id}/actions/open_ticket`, {
        data: {
          definition: {
            method: "POST",
            path: "/tickets",
            body: { summary: "{{summary}}" },
            inputs: [{ name: "summary", type: "string", required: true }],
            sideEffect: "write",
            idempotencyHeader: "Idempotency-Key",
          },
        },
      });
      expect(action.ok()).toBe(true);
      expect((await api.post(`/api/v1/integrations/pack-connections/${id}:enable`)).ok()).toBe(true);
      await api.dispose();
    });

    test("an owner adds an automatic step with a field mapping and publishes", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/services`);
      const create = page.getByRole("form", { name: m("services.admin.createTitle") });
      await create.getByLabel(m("services.admin.nameEn")).fill("New laptop");
      await create.getByLabel(m("services.admin.nameAr")).fill("حاسوب جديد");
      await create.getByRole("button", { name: m("services.admin.create") }).click();
      await page.waitForURL(/\/admin\/services\/[0-9a-f-]{36}$/);

      const form = page.getByRole("form", { name: m("services.admin.formTitle") });
      await form.getByRole("button", { name: m("services.admin.addField") }).click();
      const field = form.getByTestId("builder-field").first();
      await field.getByLabel(m("services.admin.fieldKey")).fill("device");
      await field.getByLabel(m("services.admin.inEnglish", { label: m("services.admin.fieldLabel") })).fill("Device");
      await field.getByLabel(m("services.admin.inArabic", { label: m("services.admin.fieldLabel") })).fill("الجهاز");
      await field.getByLabel(m("services.admin.required")).check();

      await form.getByRole("button", { name: m("services.admin.automation.add") }).click();
      const step = form.getByTestId("action-step").first();
      await step.getByLabel(m("services.admin.automation.action"), { exact: true }).selectOption({ label: "Service desk: open_ticket" });
      await step.getByLabel(m("services.admin.automation.nameEn")).fill("Open the ticket");
      await step.getByLabel(m("services.admin.automation.nameAr")).fill("فتح تذكرة");
      await step.getByLabel(m("services.admin.automation.inputRequired", { name: "summary" })).selectOption("form.device");
      await expect(step.getByText(m("services.admin.automation.writesOnce"))).toBeVisible();
      await expectAccessible(page, testInfo, `services-automation-builder-${locale}`);

      await form.getByRole("button", { name: m("services.admin.publish") }).click();
      await expect(form.getByText(m("services.admin.published"))).toBeVisible();
    });

    test("a colleague files the request and sees its automatic step", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/services`);
      await page.getByTestId("services-results").getByRole("link", { name: serviceName }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(serviceName);
      await page.getByRole("textbox", { name: locale === "ar" ? "الجهاز" : "Device", exact: true }).fill("Laptop 14 inch");
      await page.getByRole("button", { name: m("services.form.submit") }).click();
      await page.waitForURL(/\/requests\/[0-9a-f-]{36}$/);
      const automation = page.getByTestId("request-automation");
      await expect(automation).toContainText(stepName);
      await expect(page.getByRole("heading", { name: m("services.request.automation.title"), level: 2 })).toBeVisible();
      await expectAccessible(page, testInfo, `services-automation-request-${locale}`);
    });
  });
}
