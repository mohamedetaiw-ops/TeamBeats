import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 035 slice 1 (US2, FR-003, FR-013): an employee connects an app. The app's authorization request opens the
 * tenant's consent page, which shows the app and what it asks in the person's language; Allow returns to the app with
 * an authorization code, and Cancel returns access_denied. The token rules themselves (PKCE, refresh rotation, tenant
 * boundary) are covered by PublicApiAuthorizationServerTests.
 */
const redirect = "http://127.0.0.1:7777/callback";
const challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`app consent in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);
    let tenant: TestTenant;
    let clientId: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`consent-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const app = await api.post("/api/v1/api-access/apps", {
        data: { name: "Field notes", kind: "delegated_public", publisher: "Acme IT", scopes: ["profile:read", "content:read"], redirectUris: [redirect] },
      });
      expect(app.ok(), await app.text()).toBe(true);
      clientId = ((await app.json()) as { clientId: string }).clientId;
      await api.dispose();
    });

    test("the consent page shows the app and Allow returns a code to it", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      // The app's own address is not running in the test; answer it so the final redirect can be read.
      await page.route(`${redirect}**`, (route) => route.fulfill({ status: 200, contentType: "text/plain", body: "app" }));
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const query = new URLSearchParams({
        client_id: clientId, response_type: "code", redirect_uri: redirect, scope: "profile:read content:read", state: "s-1",
        code_challenge: challenge, code_challenge_method: "S256",
      });

      await page.goto(`${origin}/oauth/authorize?${query.toString()}`);
      await page.waitForURL(/\/app-consent\?/);
      await expect(page.getByRole("heading", { level: 1, name: m("apiAccess.consent.title", { app: "Field notes" }) })).toBeVisible();
      await expect(page.getByText(m("apiAccess.consent.publisher", { publisher: "Acme IT" }))).toBeVisible();
      await expect(page.getByText(m("apiAccess.scope.profile_read"))).toBeVisible();
      await expect(page.getByText(m("apiAccess.scope.content_read"))).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expectAccessible(page, testInfo, `app-consent-${locale}`);

      // Cancel tells the app the person refused.
      await page.getByRole("button", { name: m("apiAccess.consent.cancel") }).click();
      await page.waitForURL((url) => url.toString().startsWith(redirect));
      expect(new URL(page.url()).searchParams.get("error")).toBe("access_denied");

      // Allow saves the consent and returns to the app with a code; the next request goes straight through.
      await page.goto(`${origin}/oauth/authorize?${query.toString()}`);
      await page.waitForURL(/\/app-consent\?/);
      await page.getByRole("button", { name: m("apiAccess.consent.allow") }).click();
      await page.waitForURL((url) => url.toString().startsWith(redirect));
      const back = new URL(page.url());
      expect(back.searchParams.get("code")).toBeTruthy();
      expect(back.searchParams.get("state")).toBe("s-1");
      expect(back.searchParams.get("iss")).toBe(`${origin}/oauth`);
    });
  });
}
