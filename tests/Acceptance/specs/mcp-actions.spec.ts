import { createHash, randomBytes } from "node:crypto";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 035 slice 4 (US3, FR-019 to FR-022): an AI policy administrator allows an MCP client and turns on actions that
 * employees confirm; the client, acting for the employee through the tenant's MCP server, asks to draft a news item;
 * nothing happens until the employee opens the card in TeamBeats, sees the client and every value, and confirms. The
 * gateway rules themselves (scopes, caps, limits, kill switch, expiry) are covered by PublicMcpTests.
 */
const redirect = "http://127.0.0.1:7777/callback";
const base64url = (bytes: Buffer) => bytes.toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");

/** The JSON-RPC message of an MCP answer, sent as JSON or as one event of a stream. */
function rpc(text: string): { result?: { structuredContent?: { content?: { actionId?: string } } }; error?: unknown } {
  const data = text.includes("data:")
    ? text
        .split("\n")
        .filter((l) => l.startsWith("data:"))
        .map((l) => l.slice(5).trim())
        .join("\n")
    : text;
  return JSON.parse(data);
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`AI client actions in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);
    const appName = locale === "ar" ? "مساعد سطح المكتب" : "Desktop assistant";
    const title = locale === "ar" ? "قائمة المقصف" : "Canteen menu";
    let tenant: TestTenant;
    let clientId: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`mcp-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const app = await api.post("/api/v1/api-access/apps", {
        data: { name: appName, kind: "mcp_client", publisher: "Vendor", scopes: ["content:draft"], redirectUris: [redirect] },
      });
      expect(app.ok(), await app.text()).toBe(true);
      const created = (await app.json()) as { id: string; clientId: string };
      clientId = created.clientId;
      const consent = await api.put(`/api/v1/api-access/apps/${created.id}/admin-consent`, { data: { scopes: ["content:draft"] } });
      expect(consent.ok(), await consent.text()).toBe(true);
      await api.dispose();
    });

    test("the employee confirms a news draft an AI client asked for", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      // The AI policy administrator allows the client and the actions employees confirm.
      await page.goto(`${origin}/admin/agents/policy`);
      const external = page.getByTestId("external-ai-policy");
      await expect(external).toBeVisible();
      await page.getByLabel(m("agents.policy.external.newClient"), { exact: true }).fill(clientId);
      await page.getByRole("button", { name: m("agents.policy.external.add") }).click();
      await expect(page.getByTestId("external-ai-clients").getByRole("rowheader")).toHaveText(clientId);
      await external.getByLabel(m("agents.policy.external.writeTools")).check();
      await page.getByRole("button", { name: m("agents.policy.save") }).click();
      await expect(page.getByText(m("agents.policy.saved"))).toBeVisible();
      await expectAccessible(page, testInfo, `agents-policy-external-${locale}`);

      // The client signs in as the employee with PKCE and gets a token for the MCP server.
      // Requests go from the tenant's own page, as the test host names resolve only in the browser.
      const send = (path: string, init: { method: string; headers?: Record<string, string>; body?: string }) =>
        page.evaluate(
          async ({ path, init }) => {
            const response = await fetch(path, init);
            return { status: response.status, text: await response.text() };
          },
          { path, init },
        );
      const consented = await send("/api/v1/me/app-consent", {
        method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ clientId, redirectUri: redirect, scopes: ["content:draft"] }),
      });
      expect(consented.status, consented.text).toBe(200);
      await page.route(`${redirect}**`, (route) => route.fulfill({ status: 200, contentType: "text/plain", body: "client" }));
      const verifier = base64url(randomBytes(32));
      const query = new URLSearchParams({
        client_id: clientId, response_type: "code", redirect_uri: redirect, scope: "content:draft", state: "s",
        code_challenge: base64url(createHash("sha256").update(verifier).digest()), code_challenge_method: "S256",
      });
      await page.goto(`${origin}/oauth/authorize?${query.toString()}`);
      await page.waitForURL((url) => url.toString().startsWith(redirect));
      const code = new URL(page.url()).searchParams.get("code")!;
      await page.goto(`${origin}/admin/agents/policy`);
      const token = await send("/oauth/token", {
        method: "POST", headers: { "content-type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({ grant_type: "authorization_code", client_id: clientId, code, redirect_uri: redirect, code_verifier: verifier }).toString(),
      });
      expect(token.status, token.text).toBe(200);
      const access = (JSON.parse(token.text) as { access_token: string }).access_token;

      // The client asks to draft news: nothing is created yet, the employee is asked.
      const call = await send("/mcp", {
        method: "POST",
        headers: { authorization: `Bearer ${access}`, accept: "application/json, text/event-stream", "content-type": "application/json" },
        body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "draft_news", arguments: { title, text: "Soup on Monday." } } }),
      });
      expect(call.status, call.text).toBe(200);
      const answer = rpc(call.text);
      expect(answer.error, JSON.stringify(answer.error)).toBeUndefined();
      const actionId = answer.result!.structuredContent!.content!.actionId!;

      await page.goto(`${origin}/ai-actions/${actionId}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("aiActions.title"));
      const card = page.getByTestId("ai-action");
      await expect(card.getByText(m("aiActions.external"))).toBeVisible();
      await expect(card.getByRole("definition").filter({ hasText: appName })).toBeVisible();
      await expect(card.getByRole("definition").filter({ hasText: title })).toBeVisible();
      await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expectAccessible(page, testInfo, `ai-action-${locale}`);
      await card.getByRole("button", { name: m("aiActions.confirm") }).click();
      await expect(card.getByText(m("aiActions.status.confirmed"))).toBeVisible();
    });
  });
}
