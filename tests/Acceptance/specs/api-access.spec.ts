import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 035 slices 2 and 3 (T007, T009, US1, US5, US6, FR-029): an API administrator registers an app that acts as
 * itself, gives it a secret shown once and the App reader role, and sees every consent and the usage of each app; an employee sees the apps they connected and
 * removes one. The rules behind each change are covered by PublicApiAuthorizationServerTests and PublicApiAppPrincipalTests.
 */
const redirect = "http://127.0.0.1:7777/callback";

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`API and MCP access in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`api-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const app = await api.post("/api/v1/api-access/apps", {
        data: { name: "Field notes", kind: "delegated_public", publisher: "Acme IT", scopes: ["profile:read", "content:read"], redirectUris: [redirect] },
      });
      expect(app.ok(), await app.text()).toBe(true);
      const clientId = ((await app.json()) as { clientId: string }).clientId;
      const consent = await api.post("/api/v1/me/app-consent", { data: { clientId, redirectUri: redirect, scopes: ["profile:read", "content:read"] } });
      expect(consent.ok(), await consent.text()).toBe(true);
      await api.dispose();
    });

    test("an administrator registers an app, gives it a secret and a role, and sees the consents", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin/api`);
      await expect(page.getByRole("heading", { level: 1, name: m("apiAccess.admin.title") })).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expect(page.getByRole("link", { name: "Field notes" })).toBeVisible();
      await expectAccessible(page, testInfo, `api-access-${locale}`);

      const register = page.locator("section", { has: page.getByRole("heading", { name: m("apiAccess.admin.registerTitle") }) });
      await register.getByLabel(m("apiAccess.admin.name")).fill("Nightly sync");
      await register.getByRole("checkbox", { name: "content:read" }).check();
      await register.getByRole("button", { name: m("apiAccess.admin.register") }).click();
      await expect(page.getByText(m("apiAccess.admin.registered", { name: "Nightly sync" }))).toBeVisible();
      await page.getByRole("link", { name: "Nightly sync" }).click();
      await page.waitForURL(/\/admin\/api\/apps\//);
      await expect(page.getByRole("heading", { level: 1, name: "Nightly sync" })).toBeVisible();

      // A secret is shown once, right after it is added.
      const credentials = page.locator("section", { has: page.getByRole("heading", { level: 2, name: m("apiAccess.admin.credentialsTitle") }) });
      await credentials.getByRole("radio", { name: m("apiAccess.admin.credentialKinds.secret") }).check();
      await credentials.getByRole("button", { name: m("apiAccess.admin.addCredential") }).click();
      await expect(page.getByText(m("apiAccess.admin.secretOnce"))).toBeVisible();
      await expect(page.locator(".api-access-secret code")).toHaveText(/^tbs_/);
      await page.getByRole("button", { name: m("apiAccess.admin.secretDone") }).click();
      await expect(page.locator(".api-access-secret")).toHaveCount(0);

      // The App reader role, for the whole organization.
      const roles = page.locator("section", { has: page.getByRole("heading", { level: 2, name: m("apiAccess.admin.rolesTitle") }) });
      await roles.getByLabel(m("apiAccess.admin.where")).selectOption("tenant");
      await roles.getByRole("button", { name: m("apiAccess.admin.giveRole") }).click();
      await expect(page.getByText(m("apiAccess.admin.roleGiven"))).toBeVisible();
      await expect(roles.getByRole("cell", { name: m("apiAccess.admin.wholeOrganization") })).toBeVisible();

      // Event subscriptions (US4): none yet, and the app needs the events:subscribe scope before it can have one.
      const subscriptions = page.locator("section", { has: page.getByRole("heading", { level: 2, name: m("apiAccess.admin.subscriptionsTitle") }) });
      await expect(subscriptions.getByText(m("apiAccess.admin.noSubscriptions"))).toBeVisible();
      await expect(subscriptions.getByText(m("apiAccess.admin.subscribeNeedsScope"))).toBeVisible();
      await expectAccessible(page, testInfo, `api-app-${locale}`);

      // Every consent in the organization.
      await page.goto(`${origin}/admin/api`);
      await page.getByRole("tab", { name: m("apiAccess.admin.consentsTab") }).click();
      await expect(page.getByRole("rowheader", { name: "Field notes" })).toBeVisible();

      // The usage of each app (US5), opened straight from the link in a quota alert.
      await page.goto(`${origin}/admin/api?tab=usage`);
      await expect(page.getByRole("tab", { name: m("apiAccess.admin.usageTab") })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("heading", { level: 2, name: m("apiAccess.admin.usageTitle") })).toBeVisible();
      await expect(page.getByRole("rowheader", { name: "Nightly sync" })).toBeVisible();
      await expectAccessible(page, testInfo, `api-usage-${locale}`);

      // The developer page (US7): the addresses on the organization's own host, and what its plan includes.
      await page.goto(`${origin}/admin/api?tab=developer`);
      await expect(page.getByRole("tab", { name: m("apiAccess.admin.developerTab") })).toHaveAttribute("aria-selected", "true");
      await expect(page.getByRole("heading", { level: 2, name: m("apiAccess.admin.developerTitle") })).toBeVisible();
      await expect(page.locator(".api-access-addresses code").first()).toHaveText(`${origin}/openapi/public-v1.json`);
      await expect(page.getByRole("heading", { level: 2, name: m("apiAccess.admin.scopesTitle") })).toBeVisible();
      await expectAccessible(page, testInfo, `api-developer-${locale}`);
    });

    test("an employee sees the apps they connected and removes one", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/settings`);
      await page.getByRole("link", { name: m("preferences.connected.link") }).click();
      await page.waitForURL(/\/settings\/connected-apps$/);
      await expect(page.getByRole("heading", { level: 1, name: m("apiAccess.connected.title") })).toBeVisible();
      await expect(page.getByRole("heading", { level: 2, name: "Field notes" })).toBeVisible();
      await expect(page.getByText(m("apiAccess.scope.content_read"))).toBeVisible();
      await expectAccessible(page, testInfo, `connected-apps-${locale}`);

      await page.getByRole("button", { name: m("apiAccess.connected.remove") }).click();
      await page.getByRole("button", { name: m("apiAccess.connected.remove") }).last().click();
      await expect(page.getByText(m("apiAccess.connected.none"))).toBeVisible();
    });
  });
}
