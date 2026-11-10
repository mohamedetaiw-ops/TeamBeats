import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 017 T007, T010 and T012: the Connections area. The administrator allows a host, adds a custom REST connection
 * (it starts turned off), tests it, turns it on and off, defines an action without code, reads the run history and
 * deletes the connection, in English and Arabic with axe. Nothing calls an external system: the test only checks the
 * allowlist, and no action runs. The API decides every rule.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`connections in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`conn-${locale}`, locale, "workplace_plus");
    });

    test("an administrator adds, tests, turns on and off, extends and deletes a custom REST connection", async ({ page }, testInfo) => {
      const host = `permits-${locale}.example`;
      const name = locale === "ar" ? "تصاريح المرافق" : "Facilities permits";

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin`);
      await page.getByRole("link", { name: msg(locale, "admin.nav.connections") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "connections.title"));
      await expect(page.getByText(msg(locale, "connections.list.empty"))).toBeVisible();
      await expectAccessible(page, testInfo, `connections-${locale}`);

      // The host goes on the allowlist first; the connection's address must be on it.
      await page.getByLabel(msg(locale, "connections.allowlist.host")).fill(host);
      await page.getByRole("button", { name: msg(locale, "connections.allowlist.add") }).click();
      await expect(page.getByRole("button", { name: msg(locale, "connections.allowlist.removeHost").replace("{host}", host) })).toBeVisible();

      await page.getByLabel(msg(locale, "connections.settings.name")).fill(name);
      await page.getByLabel(msg(locale, "connections.settings.baseUrl")).fill(`https://${host}/v1`);
      await page.getByLabel(msg(locale, "connections.settings.authType")).selectOption("none");
      await page.getByRole("button", { name: msg(locale, "connections.add.submit") }).click();

      await expect(page.getByRole("heading", { level: 1 })).toHaveText(name);
      await expect(page.getByText(msg(locale, "connections.status.disabled"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `connection-${locale}`);

      await page.getByRole("button", { name: msg(locale, "connections.detail.test"), exact: true }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "connections.detail.testPassed") })).toBeVisible();
      await expect(page.getByText(msg(locale, "connections.checks.host_allowed"))).toBeVisible();

      await page.getByRole("button", { name: msg(locale, "connections.detail.enable"), exact: true }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "connections.detail.enabled") })).toBeVisible();
      await expect(page.getByText(msg(locale, "connections.status.healthy"), { exact: true })).toBeVisible();
      await page.getByRole("button", { name: msg(locale, "connections.detail.disable"), exact: true }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "connections.detail.disabled") })).toBeVisible();

      // An action defined without code.
      const form = page.getByRole("form", { name: msg(locale, "connections.builder.formLabel") });
      await form.getByLabel(msg(locale, "connections.builder.name"), { exact: true }).fill("lookup_permit");
      await form.getByLabel(msg(locale, "connections.builder.path"), { exact: true }).fill("/permits/{{id}}");
      await form.getByRole("button", { name: msg(locale, "connections.builder.addInput") }).click();
      await form.getByLabel(msg(locale, "connections.builder.inputName"), { exact: true }).fill("id");
      await form.getByLabel(msg(locale, "connections.builder.output"), { exact: true }).fill("status: $.status");
      await form.getByRole("button", { name: msg(locale, "connections.builder.save"), exact: true }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "connections.builder.saved") })).toBeVisible();
      await expect(page.getByRole("button", { name: msg(locale, "connections.builder.editAction").replace("{name}", "lookup_permit") })).toBeVisible();

      // Run history shows no runs yet, and nothing to replay.
      await expect(page.getByRole("heading", { name: msg(locale, "connections.runs.title") })).toBeVisible();
      await expect(page.getByText(msg(locale, "connections.runs.empty"))).toBeVisible();
      await expectAccessible(page, testInfo, `connection-actions-${locale}`);

      const deletion = page.getByRole("region", { name: msg(locale, "connections.detail.deleteTitle") });
      await deletion.getByRole("button", { name: msg(locale, "connections.detail.delete"), exact: true }).click();
      await page.getByRole("button", { name: msg(locale, "connections.detail.deleteConfirm"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "connections.title"));
      await expect(page.getByText(msg(locale, "connections.list.empty"))).toBeVisible();
    });
  });
}
