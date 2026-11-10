import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 017 T005, T007, T010 and T012: the Connections area. The administrator allows a host, adds a custom REST connection
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

      // The action declares its inputs, so it can be registered as an agent tool.
      const tools = page.getByRole("form", { name: msg(locale, "connections.tools.registerTitle") });
      await tools.getByLabel(msg(locale, "connections.tools.name"), { exact: true }).fill("permit_status");
      await tools.getByLabel(msg(locale, "connections.tools.description"), { exact: true }).fill("Looks up the status of a parking permit by its ID.");
      await tools.getByLabel(msg(locale, "connections.tools.titleEn"), { exact: true }).fill("Permit status");
      await tools.getByLabel(msg(locale, "connections.tools.titleAr"), { exact: true }).fill("حالة التصريح");
      await tools.getByRole("button", { name: msg(locale, "connections.tools.register"), exact: true }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "connections.tools.registered") })).toBeVisible();
      await expect(page.getByRole("heading", { name: locale === "ar" ? "حالة التصريح" : "Permit status", level: 3 })).toBeVisible();

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

    test("an administrator adds a Microsoft 365 connection, reads its permissions, turns a feature off and on and deletes it", async ({ page }, testInfo) => {
      const name = locale === "ar" ? "مستأجر الشركة" : "Company tenant";
      const tenantId = locale === "ar" ? "7b2a4c11-3f5e-4d6a-9b8c-0d1e2f3a4b5c" : "6a1b3c22-4e5f-4a7b-8c9d-1e2f3a4b5c6d";
      const mail = msg(locale, "connections.m365.feature.mail");

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/connections`);
      const add = page.getByRole("form", { name: msg(locale, "connections.m365.addTitle") });
      await add.getByLabel(msg(locale, "connections.m365.tenantId")).fill(tenantId);
      await add.getByLabel(msg(locale, "connections.m365.name")).fill(name);
      await add.getByLabel(mail, { exact: true }).check();
      await add.getByRole("button", { name: msg(locale, "connections.m365.add") }).click();

      await expect(page.getByRole("heading", { level: 1 })).toHaveText(name);
      await expect(page.getByText(msg(locale, "connections.status.needs_consent"), { exact: true })).toBeVisible();
      await expect(page.getByText(msg(locale, "connections.m365.permissions.mail_read_basic"))).toBeVisible();
      await expect(page.getByRole("link", { name: msg(locale, "connections.m365.grantConsent").replace("{feature}", mail) })).toBeVisible();
      await expectAccessible(page, testInfo, `m365-connection-${locale}`);

      await page.getByRole("button", { name: msg(locale, "connections.m365.turnOffFeature").replace("{feature}", mail) }).click();
      await expect(page.getByRole("button", { name: msg(locale, "connections.m365.turnOnFeature").replace("{feature}", mail) })).toBeVisible();
      await page.getByRole("button", { name: msg(locale, "connections.m365.turnOnFeature").replace("{feature}", mail) }).click();
      await expect(page.getByRole("form", { name: msg(locale, "connections.m365.optionsFor").replace("{feature}", mail) })).toBeVisible();

      const deletion = page.getByRole("region", { name: msg(locale, "connections.detail.deleteTitle") });
      await deletion.getByRole("button", { name: msg(locale, "connections.detail.delete"), exact: true }).click();
      await page.getByRole("button", { name: msg(locale, "connections.detail.deleteConfirm"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "connections.title"));
      await expect(page.getByText(msg(locale, "connections.m365.empty"))).toBeVisible();
    });

    test("an administrator adds a webhook that sends an event and receives deliveries, reads its inbound address and deletes it", async ({ page }, testInfo) => {
      const host = `hooks-${locale}.example`;
      const name = locale === "ar" ? "تنبيهات التصاريح" : "Permit alerts";
      const event = msg(locale, "connections.webhooks.events.RequestCompleted");

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/connections`);
      await page.getByLabel(msg(locale, "connections.allowlist.host")).fill(host);
      await page.getByRole("button", { name: msg(locale, "connections.allowlist.add") }).click();
      await expect(page.getByRole("button", { name: msg(locale, "connections.allowlist.removeHost").replace("{host}", host) })).toBeVisible();

      const form = page.getByRole("form", { name: msg(locale, "connections.webhooks.formLabel") });
      await form.getByLabel(msg(locale, "connections.webhooks.name")).fill(name);
      await form.getByLabel(msg(locale, "connections.webhooks.url")).fill(`https://${host}/teambeats`);
      await form.getByLabel(msg(locale, "connections.webhooks.secretRef")).fill(`secret://tenants/${tenant.id}/permit-hook`);
      await form.getByLabel(event, { exact: true }).check();
      await expect(form.getByLabel(msg(locale, "connections.webhooks.fieldOf").replace("{field}", "status").replace("{event}", event))).toBeChecked();
      await form.getByLabel(msg(locale, "connections.webhooks.inboundOn")).check();
      await form.getByLabel(msg(locale, "connections.webhooks.targetName")).fill("permits.approved");
      await form.getByLabel(msg(locale, "connections.webhooks.fields"), { exact: true }).fill("status: $.permit.status");
      await expectAccessible(page, testInfo, `webhook-add-${locale}`);
      await form.getByRole("button", { name: msg(locale, "connections.webhooks.add") }).click();

      await expect(page.getByRole("heading", { level: 1 })).toHaveText(name);
      await expect(page.getByText(msg(locale, "connections.status.disabled"), { exact: true })).toBeVisible();
      await expect(page.getByRole("heading", { name: msg(locale, "connections.webhooks.inboundTitle"), level: 2 })).toBeVisible();
      await expect(page.getByText("/api/v1/integrations/inbound/")).toBeVisible();
      await expectAccessible(page, testInfo, `webhook-${locale}`);

      const deletion = page.getByRole("region", { name: msg(locale, "connections.detail.deleteTitle") });
      await deletion.getByRole("button", { name: msg(locale, "connections.detail.delete"), exact: true }).click();
      await page.getByRole("button", { name: msg(locale, "connections.detail.deleteConfirm"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "connections.title"));
    });

    test("an administrator adds an on-premises Active Directory, makes an enrolment code for its sync agent and deletes it", async ({ page }, testInfo) => {
      const name = locale === "ar" ? "دليل المكتب الرئيسي" : "Head office directory";

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/connections`);
      const form = page.getByRole("form", { name: msg(locale, "connections.onprem.formLabel") });
      await form.getByLabel(msg(locale, "connections.onprem.name")).fill(name);
      await form.getByLabel(msg(locale, "connections.onprem.host")).fill(`dc01.${locale}.corp.example`);
      await form.getByLabel(msg(locale, "connections.onprem.port")).selectOption("3269");
      await form.getByLabel(msg(locale, "connections.onprem.baseDn")).fill("DC=corp,DC=example");
      await expectAccessible(page, testInfo, `onprem-add-${locale}`);
      await form.getByRole("button", { name: msg(locale, "connections.onprem.add") }).click();

      await expect(page.getByRole("heading", { level: 1 })).toHaveText(name);
      const agents = page.getByRole("region", { name: msg(locale, "connections.agents.title") });
      await expect(agents.getByText(msg(locale, "connections.agents.empty"))).toBeVisible();
      await agents.getByRole("button", { name: msg(locale, "connections.agents.makeCode") }).click();
      await expect(agents.getByText(msg(locale, "connections.agents.codeMade"))).toBeVisible();
      await expect(agents.locator("code")).toHaveText(/^[A-Z2-9]{5}(-[A-Z2-9]{5}){3}$/);
      await expectAccessible(page, testInfo, `onprem-${locale}`);

      const deletion = page.getByRole("region", { name: msg(locale, "connections.detail.deleteTitle") });
      await deletion.getByRole("button", { name: msg(locale, "connections.detail.delete"), exact: true }).click();
      await page.getByRole("button", { name: msg(locale, "connections.detail.deleteConfirm"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "connections.title"));
    });
  });
}
