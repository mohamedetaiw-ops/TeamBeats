import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 026 T029, T031: compliance projects. The administrator starts a project from the ISO 27001:2022 control set,
 * reads its readiness, moves a control on and attaches an audit export as evidence, which shows its SHA-256. English
 * and Arabic, with axe on every page.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`compliance projects in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let projectUrl = "";

    test.beforeAll(async () => {
      tenant = await createTenant(`cmp-${locale}`, locale, "enterprise_gov");
    });

    test("an administrator starts a project from the ISO 27001 control set and reads its readiness", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/compliance`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "compliance.pageTitle"));
      await expectAccessible(page, testInfo, `compliance-list-${locale}`);

      const name = locale === "ar" ? "تدقيق ISO 27001 لعام 2027" : "ISO 27001 surveillance audit 2027";
      await page.getByLabel(msg(locale, "compliance.create.name"), { exact: true }).fill(name);
      await page.getByLabel(msg(locale, "compliance.create.framework"), { exact: true }).selectOption("iso27001");
      await page.getByRole("button", { name: msg(locale, "compliance.create.submit") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(name);
      projectUrl = page.url();

      await expect(page.getByRole("heading", { name: msg(locale, "compliance.readiness.title") })).toBeVisible();
      await expect(page.getByRole("meter", { name: msg(locale, "compliance.status.not_started") })).toHaveAttribute("aria-valuenow", "93");
      await expect(page.getByRole("link", { name: locale === "ar" ? "التحكم في الوصول" : "Access control", exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `compliance-project-${locale}`);
    });

    test("a control moves on and an audit export attached as evidence shows its hash", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const accepted = await api.post("/api/v1/audit/exports", { data: { format: "csv" } });
      expect(accepted.status()).toBe(202);
      const exportId = ((await accepted.json()) as { id: string }).id;
      await expect
        .poll(async () => ((await (await api.get(`/api/v1/audit/exports/${exportId}`)).json()) as { status: string }).status, { timeout: 60_000 })
        .toBe("completed");
      await api.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(projectUrl);
      await page.getByRole("link", { name: locale === "ar" ? "التحكم في الوصول" : "Access control", exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(locale === "ar" ? "التحكم في الوصول" : "Access control");
      await expectAccessible(page, testInfo, `compliance-control-${locale}`);

      await page.getByLabel(msg(locale, "compliance.control.status"), { exact: true }).selectOption("in_progress");
      await page.getByRole("button", { name: msg(locale, "compliance.control.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "compliance.control.saved") })).toBeVisible();

      const task = locale === "ar" ? "تصدير تغييرات أدوار المسؤولين" : "Export admin role changes";
      await page.getByLabel(msg(locale, "compliance.tasks.taskTitle"), { exact: true }).fill(task);
      await page.getByRole("button", { name: msg(locale, "compliance.tasks.add") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "compliance.tasks.added") })).toBeVisible();

      const item = page.getByRole("listitem").filter({ hasText: task });
      await item.getByRole("button", { name: msg(locale, "compliance.tasks.attach") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "compliance.tasks.attached") })).toBeVisible();
      await expect(item.getByText(msg(locale, "compliance.tasks.sha256"))).toBeVisible();
      await expect(item.getByText(exportId)).toBeVisible();
      await expectAccessible(page, testInfo, `compliance-evidence-${locale}`);
    });
  });
}
