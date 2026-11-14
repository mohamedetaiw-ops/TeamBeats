import { request } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { sessionToken, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 011 R2 US9 in the browser: the integration administrator allows a SharePoint site, a library manager connects
 * its document library, and the library page lists SharePoint files with the person's own access. The development
 * host's fake Microsoft 365 (scenario staging-demo.json) serves the sites; the person's own Microsoft 365 sign-in
 * needs HTTPS on the tenant host, so the browser's listing call is answered here with the documented shape after the
 * page has shown the connect state. The server side is covered by tests/Integration/SharePointLibrariesTests.cs.
 */
const directory = "5a7e0c3d-1f2b-4c6d-9e8f-0a1b2c3d4e5f";

async function grantConsent(tenant: TestTenant, connectionId: string, feature: string): Promise<void> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const token = await sessionToken(tenant);
  const admin = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
  const start = await admin.get(`/api/v1/integrations/connections/${connectionId}/features/${feature}/consent`, { maxRedirects: 0 });
  const state = new URL(start.headers()["location"]!, stack.apiOrigin).searchParams.get("state")!;
  const binding = /tb_consent=([^;]+)/.exec(start.headers()["set-cookie"] ?? "")?.[1] ?? "";
  const platform = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": `platform.${stack.baseDomain}` } });
  const relay = await platform.get(`/api/v1/integrations/consent/callback?admin_consent=True&tenant=${directory}&state=${encodeURIComponent(state)}`, {
    maxRedirects: 0,
  });
  const back = new URL(relay.headers()["location"]!, stack.apiOrigin);
  const bound = await request.newContext({
    baseURL: stack.apiOrigin,
    extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}; tb_consent=${binding}` },
  });
  const done = await bound.get(back.pathname + back.search, { maxRedirects: 0 });
  expect(done.headers()["location"]).toMatch(/consent=granted$/);
  await Promise.all([admin.dispose(), platform.dispose(), bound.dispose()]);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`connected SharePoint libraries in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let connectionId: string;
    const m = (key: string) => msg(locale, key);

    test.beforeAll(async () => {
      tenant = await createTenant(`spl-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/integrations/connections", {
        data: { kind: "microsoft365", externalTenantId: directory, displayName: "Demo Microsoft 365", features: ["sharepoint_documents"] },
      });
      expect(created.status()).toBe(201);
      connectionId = ((await created.json()) as { id: string }).id;
      await grantConsent(tenant, connectionId, "sharepoint_documents");
    });

    test("an administrator allows a site and a manager connects its library", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/connections/microsoft365/${connectionId}`);
      const sites = page.getByRole("region", { name: m("connections.m365.sites.title") });
      await expect(sites.getByText(m("connections.m365.sites.empty"))).toBeVisible();

      // A site the customer has not granted to the application is refused; a granted one is added.
      await sites.getByLabel(m("connections.m365.sites.url")).fill("https://teambeatsdemo.sharepoint.com/sites/finance");
      await sites.getByRole("button", { name: m("connections.m365.sites.add") }).click();
      await expect(sites.getByRole("alert")).toBeVisible();
      await sites.getByLabel(m("connections.m365.sites.url")).fill("https://teambeatsdemo.sharepoint.com/sites/hr");
      await sites.getByRole("button", { name: m("connections.m365.sites.add") }).click();
      await expect(sites.getByRole("link", { name: "Human Resources" })).toBeVisible();
      await expectAccessible(page, testInfo, `sharepoint-sites-${locale}`);

      // The manager connects the document library for the whole organization.
      await page.goto(`${hostOrigin(tenant.key)}/documents`);
      const connect = page.getByRole("region", { name: m("documents.connect.title") });
      await connect.getByLabel(m("documents.connect.name"), { exact: true }).fill(locale === "ar" ? "سياسات الموارد البشرية" : "HR policies");
      await expect(connect.getByLabel(m("documents.connect.library"))).toHaveValue(/\|/);
      // The listing answers as for a person who has not connected Microsoft 365 yet, then as for one who has.
      let state = "not_connected";
      await page.route("**/api/v1/documents/libraries/*/connected/items**", (route) => {
        const folder = new URL(route.request().url()).searchParams.get("folder");
        const items =
          state !== "ok"
            ? []
            : folder
              ? [{ id: "b!d~02", name: "Employee handbook.pdf", folder: false, mediaType: "application/pdf", size: 20480, modifiedAt: new Date().toISOString(), webLink: "https://teambeatsdemo.sharepoint.com/x" }]
              : [{ id: "b!d~01", name: "Policies", folder: true, mediaType: null, size: null, modifiedAt: new Date().toISOString(), webLink: null }];
        return route.fulfill({ contentType: "application/json", body: JSON.stringify({ state, items, nextCursor: null }) });
      });
      await connect.getByRole("button", { name: m("documents.connect.submit") }).click();
      await expect(page).toHaveURL(/\/documents\/libraries\/[0-9a-f-]{36}$/);
      const browser = page.getByTestId("connected-library");
      await expect(browser.getByText(m("documents.connected.badge"), { exact: true })).toBeVisible();
      await expect(browser.getByRole("heading", { name: m("documents.connected.notHereTitle") })).toBeVisible();

      // Without their own Microsoft 365 sign-in the person is asked to connect.
      const connectLink = browser.getByRole("link", { name: m("documents.connected.connectAction") });
      await expect(connectLink).toHaveAttribute("href", /\/api\/v1\/me\/outlook\/connect\?returnTo=%2Fdocuments%2Flibraries%2F/);
      await expectAccessible(page, testInfo, `connected-library-connect-${locale}`);

      // Signed in to Microsoft 365, the files SharePoint gives them are listed; a folder opens in place.
      state = "ok";
      await page.reload();
      await browser.getByRole("link", { name: "Policies", exact: true }).click();
      await expect(browser.getByRole("link", { name: "Employee handbook.pdf", exact: true })).toHaveAttribute("href", /\/connected\/content\?item=b!d~02$/);
      await expect(page.getByRole("navigation", { name: m("documents.connected.path") }).getByRole("link", { name: "Policies" })).toBeVisible();
      await expectAccessible(page, testInfo, `connected-library-${locale}`);
    });
  });
}
