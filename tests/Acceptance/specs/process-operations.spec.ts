import { request, type APIRequestContext } from "@playwright/test";
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

const model = `<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
  <bpmn:process id="p" name="Purchase">
    <bpmn:startEvent id="s"/>
    <bpmn:userTask id="head" name="Head review"><bpmn:extensionElements><tb:assignment assignee="headOfUnit"/><tb:name xml:lang="ar">مراجعة الرئيس</tb:name></bpmn:extensionElements></bpmn:userTask>
    <bpmn:endEvent id="e"/>
    <bpmn:sequenceFlow id="f1" sourceRef="s" targetRef="head"/><bpmn:sequenceFlow id="f2" sourceRef="head" targetRef="e"/>
  </bpmn:process>
</bpmn:definitions>`;

/**
 * Spec 024 T016 (US5): an operator finds an instance stopped by an incident, skips the step with a reason, and reads
 * the monitoring numbers of the process. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`process operations in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`po-${locale}`, locale, "workplace_plus");
      const admin = await signedIn(tenant, tenant.adminEmail);
      const me = ((await (await admin.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
      for (const roleKey of ["workflow_designer", "process_operator"]) {
        const assigned = await admin.post("/api/v1/authz/assignments", { data: { subjectType: "user", subjectId: me, roleKey, scopeType: "tenant", scopeId: null } });
        expect(assigned.ok()).toBeTruthy();
      }
      expect((await admin.put("/api/v1/workflow/processes/purchase", { data: { bpmnXml: model } })).ok()).toBeTruthy();
      expect((await admin.post("/api/v1/workflow/processes/purchase:publish")).ok()).toBeTruthy();
      const started = await admin.post("/api/v1/workflow/processes/purchase/instances", { data: { businessKey: "PO-1001", variables: { headOfUnit: null } } });
      expect(started.status()).toBe(201);
      await admin.dispose();
    });

    test("an operator skips a stopped step and reads the monitoring numbers", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/process-operations`);
      await expect(page.getByRole("heading", { level: 1, name: m("processes.operations.pageTitle") })).toBeVisible();

      await page.getByLabel(m("processes.operations.withIncident")).check();
      await page.getByRole("button", { name: m("processes.operations.search"), exact: true }).click();
      const row = page.getByRole("button", { name: "PO-1001" });
      await expect(row).toBeVisible();
      await expectAccessible(page, testInfo, "process instances");

      await row.click();
      const drawer = page.getByRole("dialog");
      await expect(drawer.getByText(m("processes.operations.incidentStates.open"), { exact: true })).toBeVisible();
      await drawer.getByLabel(m("processes.operations.reason")).fill(locale === "ar" ? "تمت الموافقة هاتفيا" : "Approved by phone");
      await expectAccessible(page, testInfo, "process instance");
      await drawer.getByRole("button", { name: m("processes.operations.resolve.skipped") }).click();
      await expect(drawer.getByRole("status")).toHaveText(m("processes.operations.resolved.skipped"));
      await expect(drawer.getByText(m("processes.operations.nothingWaiting"))).toBeVisible();
      await drawer.getByRole("button", { name: m("processes.operations.close") }).first().click();

      await page.getByRole("tab", { name: m("processes.operations.monitoringTab") }).click();
      await page.getByRole("button", { name: m("processes.monitoring.show") }).click();
      const counts = page.getByRole("list", { name: m("processes.monitoring.counts") });
      await expect(counts).toBeVisible();
      await expect(counts.getByText(m("processes.monitoring.completed"))).toBeVisible();
      await expectAccessible(page, testInfo, "process monitoring");
    });
    test("a designer builds, tests and publishes a decision table", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/decisions`);
      await expect(page.getByRole("heading", { level: 1, name: m("processes.decisions.pageTitle") })).toBeVisible();
      await page.getByLabel(m("processes.decisions.key")).fill("band");
      await page.getByLabel(m("processes.decisions.nameEn")).fill("Approval band");
      await page.getByLabel(m("processes.decisions.nameAr")).fill("فئة الاعتماد");
      await page.getByLabel(m("processes.decisions.inputExpression", { number: "1" })).fill("amount");
      await page.getByLabel(m("processes.decisions.outputName", { number: "1" })).fill("approver");
      await page.getByLabel(m("processes.decisions.inputCell", { rule: "1", number: "1" })).fill("< 1000");
      await page.getByLabel(m("processes.decisions.outputCell", { rule: "1", number: "1" })).fill('"manager"');
      await page.getByRole("button", { name: m("processes.decisions.addRule") }).click();
      await page.getByLabel(m("processes.decisions.inputCell", { rule: "2", number: "1" })).fill(">= 1000");
      await page.getByLabel(m("processes.decisions.outputCell", { rule: "2", number: "1" })).fill('"head"');
      await expectAccessible(page, testInfo, "decision table");
      await page.getByRole("button", { name: m("processes.decisions.save") }).click();
      await expect(page.getByRole("main").getByRole("status")).toHaveText(m("processes.decisions.saved"));
      await page.getByRole("button", { name: m("processes.decisions.publish"), exact: true }).click();
      await expect(page.getByRole("main").getByRole("status")).toHaveText(m("processes.decisions.published", { version: "1" }));

      await page.getByLabel(m("processes.decisions.testRows")).fill('{"amount": 5000}\n{"amount": 10}');
      await page.getByRole("button", { name: m("processes.decisions.runTest") }).click();
      const results = page.getByRole("list", { name: m("processes.decisions.testResults") });
      await expect(results.getByText('"head"')).toBeVisible();
      await expect(results.getByText('"manager"')).toBeVisible();
      await expectAccessible(page, testInfo, "decision test");
    });
    test("a designer draws, saves and publishes a process with the keyboard friendly modeler", async ({ page }, testInfo) => {
      const md = (key: string, values: Record<string, string> = {}) => m(`processes.modeler.${key}`, values);
      const end = `${md("kinds.endEvent")} (end)`;
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/processes`);
      await expect(page.getByRole("heading", { level: 1, name: md("pageTitle") })).toBeVisible();
      await expect(page.getByRole("link", { name: "Purchase" })).toBeVisible();
      await page.getByLabel(md("key"), { exact: true }).fill("approval");
      await page.getByLabel(md("processNameEn")).fill("Approval");
      await page.getByLabel(md("processNameAr")).fill("الموافقة");

      await page.locator('.react-flow__node[data-id="start"]').click();
      await page.getByRole("button", { name: md("add", { kind: md("kinds.userTask") }) }).click();
      const properties = page.locator(".processes-properties");
      await properties.getByLabel(md("nameEn"), { exact: true }).fill("Manager review");
      await properties.getByLabel(md("nameAr"), { exact: true }).fill("مراجعة المدير");
      await properties.getByLabel(md("lineManager")).check();
      await properties.getByLabel(md("connectTo")).selectOption({ label: end });
      await properties.getByRole("button", { name: md("connect"), exact: true }).click();
      await expect(properties.getByRole("button", { name: md("removeFlowTo", { to: end }) })).toBeVisible();

      await page.locator('.react-flow__node[data-id="start"]').click();
      await properties.getByRole("button", { name: md("removeFlowTo", { to: end }) }).click();
      const outline = page.getByRole("region", { name: md("outline") });
      await expect(outline.getByRole("button", { name: md("kindAndName", { kind: md("kinds.userTask"), name: locale === "ar" ? "مراجعة المدير" : "Manager review" }) })).toBeVisible();
      await expectAccessible(page, testInfo, "process modeler");

      await page.getByRole("button", { name: md("save") }).click();
      await expect(page.getByRole("main").getByRole("status")).toHaveText(md("saved"));
      await page.getByRole("button", { name: md("publish"), exact: true }).click();
      await expect(page.getByRole("main").getByRole("status")).toHaveText(md("published", { version: "1" }));

      await page.getByLabel(md("testVariables")).fill('{"amount": 5000}');
      await page.getByRole("button", { name: md("runTest") }).click();
      await expect(page.getByRole("heading", { level: 3, name: md("testOutcomeTitles.completed") })).toBeVisible();
      const steps = page.getByRole("list", { name: md("testSteps") });
      await expect(steps.getByText(md("testEvents.assigned", { who: md("testLineManager") }))).toBeVisible();
      await expectAccessible(page, testInfo, "process test run");

      await page.getByRole("button", { name: md("showXml") }).click();
      await expect(page.getByLabel(md("xml"))).toHaveValue(/tb:assignment lineManager="true"/);
      await page.getByRole("button", { name: md("showDiagram") }).click();
      await expect(page.locator(".react-flow__node")).toHaveCount(3);
    });
  });
}
