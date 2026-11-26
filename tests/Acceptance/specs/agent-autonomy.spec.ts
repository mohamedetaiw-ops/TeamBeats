import { request, type APIRequestContext, type Page } from "@playwright/test";
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

/** A process whose one step is an agent task; the agent ID is fictional, since publishing does not run it. */
const model = (agent: string) => `<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
  <bpmn:process id="p" name="Welcome notes">
    <bpmn:startEvent id="s"/>
    <bpmn:serviceTask id="agent" name="Write the welcome note"><bpmn:extensionElements><tb:name xml:lang="ar">كتابة ملاحظة الترحيب</tb:name><tb:agent id="${agent}" tools="knowledge.search" minutes="10"/><tb:input name="request" source="ask"/></bpmn:extensionElements></bpmn:serviceTask>
    <bpmn:endEvent id="e"/>
    <bpmn:sequenceFlow id="f1" sourceRef="s" targetRef="agent"/><bpmn:sequenceFlow id="f2" sourceRef="agent" targetRef="e"/>
  </bpmn:process>
</bpmn:definitions>`;

async function chooseType(page: Page, locale: LaunchLocale, kind: string) {
  await page.getByRole("link", { name: msg(locale, "connections.overview.add") }).first().click();
  await page.getByRole("link", { name: msg(locale, `connections.kinds.${kind}`), exact: true }).click();
  await expect(page.locator('[aria-current="step"]')).toContainText(msg(locale, "connections.wizard.steps.setup"));
}

/**
 * Spec 027 R3 T024: the integration administrator adds an MCP server connection and finds its tool panel, and a
 * process owner approves a version with an agent task, after which the same person cannot sign the AI policy part.
 * In English and Arabic, with axe. Nothing calls an external system. All content is fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`agent autonomy in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`l4-${locale}`, locale, "enterprise_gov");
      const admin = await signedIn(tenant, tenant.adminEmail);
      const me = ((await (await admin.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
      for (const roleKey of ["workflow_designer", "ai_policy_administrator"]) {
        const assigned = await admin.post("/api/v1/authz/assignments", { data: { subjectType: "user", subjectId: me, roleKey, scopeType: "tenant", scopeId: null } });
        expect(assigned.ok()).toBeTruthy();
      }
      expect((await admin.put("/api/v1/workflow/processes/welcome", { data: { bpmnXml: model("0193a0de-4c1f-7a20-9b3e-5d2f1c8e7a10") } })).ok()).toBeTruthy();
      expect((await admin.post("/api/v1/workflow/processes/welcome:publish")).ok()).toBeTruthy();
      await admin.dispose();
    });

    test("an administrator adds an MCP server and finds its tools waiting to be read", async ({ page }, testInfo) => {
      const host = `tools-${locale}.example`;
      const name = locale === "ar" ? "أدوات المرافق" : "Facilities tools";
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/connections`);
      await page.getByLabel(m("connections.allowlist.host")).fill(host);
      await page.getByRole("button", { name: m("connections.allowlist.add") }).click();
      await expect(page.getByRole("button", { name: m("connections.allowlist.removeHost").replace("{host}", host) })).toBeVisible();

      await chooseType(page, locale, "mcp_server");
      const add = page.getByRole("region", { name: m("connections.ticketing.mcp_server.addTitle") });
      await expect(add.getByLabel(m("connections.settings.authType")).locator("option")).toHaveCount(4);
      await add.getByLabel(m("connections.settings.name")).fill(name);
      await add.getByLabel(m("connections.settings.baseUrl")).fill(`https://${host}/mcp`);
      await add.getByLabel(m("connections.settings.authType")).selectOption("none");
      await add.getByRole("button", { name: m("connections.wizard.setup.save") }).click();
      await expect(page.locator('[aria-current="step"]')).toContainText(m("connections.wizard.steps.test"));
      await page.getByRole("link", { name: m("connections.wizard.test.changeSettings") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(name);

      const tools = page.getByRole("region", { name: m("connections.mcpTools.title") });
      await expect(tools.getByText(m("connections.mcpTools.empty"))).toBeVisible();
      await expect(tools.getByRole("button", { name: m("connections.mcpTools.refresh") })).toBeVisible();
      await expect(page.getByRole("region", { name: m("connections.tools.title") })).toHaveCount(0);
      await expectAccessible(page, testInfo, `mcp-connection-${locale}`);
    });

    test("a process owner approves agent steps and cannot also sign as AI policy administrator", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/processes/welcome`);
      const panel = page.getByRole("region", { name: m("processes.autonomy.title", { version: "1" }) });
      await expect(panel.getByText(m("processes.autonomy.waiting"))).toBeVisible();
      await expect(panel.getByText(locale === "ar" ? "كتابة ملاحظة الترحيب" : "Write the welcome note")).toBeVisible();
      await expectAccessible(page, testInfo, `process-autonomy-${locale}`);

      await panel.getByRole("button", { name: m("processes.autonomy.approveAs.owner") }).click();
      await expect(panel.getByRole("button", { name: m("processes.autonomy.approveAs.owner") })).toHaveCount(0);
      // The same person signed the owner part, so the AI policy part waits for someone else.
      await expect(panel.getByRole("button", { name: m("processes.autonomy.approveAs.policy") })).toHaveCount(0);
      await expect(panel.getByText(m("processes.autonomy.notSigned"))).toBeVisible();
      await expect(panel.getByText(m("processes.autonomy.waiting"))).toBeVisible();
    });
  });
}
