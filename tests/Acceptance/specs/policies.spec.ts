import { request } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 025 T011 (first part): a policy approved by a second person and in force, with an attestation campaign. The
 * administrator finds it in My policies, reads it with numbered clauses, anchors and the note on the binding language,
 * accepts it on the policy page, sees it in the register and in the task inbox no more. In English and Arabic, with axe
 * on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policies in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let policyId: string;
    const other = locale === "ar" ? "en" : "ar";
    const names = { en: "Gifts and hospitality", ar: "الهدايا والضيافة" };
    const clause = (en: string, ar: string, level: number) => ({
      level,
      titles: { en, ar },
      bodies: {
        en: { blocks: [{ type: "paragraph", children: [{ text: `${en} applies to every employee.` }] }] },
        ar: { blocks: [{ type: "paragraph", children: [{ text: `ينطبق ${ar} على كل موظف.` }] }] },
      },
    });

    test.beforeAll(async () => {
      tenant = await createTenant(`pol-${locale}`, locale, "workplace", ["legal"]);
      const host = `${tenant.key}.${stack.baseDomain}`;
      const legal = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
      expect((await legal.post("/dev/signin", { data: { email: `legal@${tenant.key}.example` } })).ok()).toBe(true);
      const legalId = ((await (await legal.get("/api/v1/me")).json()) as { id: string }).id;

      const admin = await tenantApi(tenant);
      const created = await admin.post("/api/v1/policies", {
        data: { code: "POL-001", names, bindingLocale: other, route: [{ approvers: [legalId] }] },
      });
      expect(created.status(), await created.text()).toBe(201);
      policyId = ((await created.json()) as { id: string }).id;
      const draft = await admin.put(`/api/v1/policies/${policyId}/draft`, {
        data: {
          titles: names,
          clauses: [clause("Scope", "النطاق", 1), clause("Gifts from suppliers", "هدايا الموردين", 1), clause("Limits", "الحدود", 2)],
          material: true,
        },
      });
      expect(draft.ok(), await draft.text()).toBe(true);
      expect((await admin.post(`/api/v1/policies/${policyId}/submit`, { data: {} })).ok()).toBe(true);

      const tasks = (await (await legal.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
      const task = tasks.find((t) => t.source === "policy_approval");
      expect(task).toBeTruthy();
      const decided = await legal.post(`/api/v1/tasks/${task!.taskId}/decision`, { data: { outcome: "approve" } });
      expect(decided.ok(), await decided.text()).toBe(true);
      await legal.dispose();

      const campaign = await admin.post(`/api/v1/policies/${policyId}/campaigns`, { data: {} });
      expect(campaign.status(), await campaign.text()).toBe(201);
      await admin.dispose();
    });

    test("find, read and accept a policy", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);

      // The inbox links the attestation to the policy page.
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      await expect(page.getByTestId("task-inbox").getByRole("link", { name: names[locale] })).toBeVisible();

      await page.goto(`${hostOrigin(tenant.key)}/policies/my`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "policies.my.title"));
      await expectAccessible(page, testInfo, `policies-my-${locale}`);
      await page.getByTestId("my-policies-due").getByRole("link", { name: names[locale] }).click();
      await page.waitForURL(/\/policies\/[0-9a-f-]{36}\?attest=1$/);

      // Numbered clauses with anchors, and the note that the other language binds.
      const doc = page.getByTestId("policy-document");
      await expect(doc.getByRole("heading", { level: 1 })).toHaveText(names[locale]);
      await expect(doc.getByRole("heading", { level: 2 }).nth(1)).toContainText("2");
      await expect(doc.getByRole("heading", { level: 3 })).toContainText("2.1");
      const limits = doc.locator("section.policies-clause").nth(2);
      await expect(limits).toHaveAttribute("id", /^clause-[a-z0-9]{10}$/);
      await expect(page.getByTestId("binding-note")).toBeVisible();
      await expectAccessible(page, testInfo, `policies-page-${locale}`);

      // Accept on the page.
      const panel = page.getByTestId("attest-panel");
      await panel.getByRole("button", { name: msg(locale, "policies.attest.submit") }).click();
      await expect(panel.getByRole("alert")).toHaveText(msg(locale, "policies.attest.confirmFirst"));
      await panel.getByLabel(msg(locale, "policies.attest.confirm")).check();
      await panel.getByRole("button", { name: msg(locale, "policies.attest.submit") }).click();
      await expect(panel.getByRole("status")).toBeVisible();
      await expectAccessible(page, testInfo, `policies-accepted-${locale}`);

      // "Ask about this policy" opens the assistant kept to this policy (spec 025 FR-011).
      const policyUrl = page.url();
      await page.getByTestId("policy-ask").click();
      await page.waitForURL(/\/assistant\?policy=[0-9a-f-]{36}$/);
      await expect(page.getByTestId("assistant-scope").getByRole("link", { name: names[locale] })).toBeVisible();
      await expectAccessible(page, testInfo, `policies-ask-${locale}`);
      await page.goto(policyUrl);

      // The version history and the numbered version page.
      await page.getByTestId("policy-versions").getByRole("link").first().click();
      await page.waitForURL(/\/versions\/1$/);
      await expect(page.getByTestId("policy-document")).toBeVisible();
      await expect(page.getByTestId("superseded-banner")).toHaveCount(0);
      await expectAccessible(page, testInfo, `policies-version-${locale}`);

      // The register lists it as published; My policies shows it accepted.
      await page.goto(`${hostOrigin(tenant.key)}/policies`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "policies.register.title"));
      const row = page.getByTestId("policy-register").getByRole("row").filter({ hasText: "POL-001" });
      await expect(row).toContainText(msg(locale, "policies.status.published"));
      await expectAccessible(page, testInfo, `policies-register-${locale}`);
      await page.goto(`${hostOrigin(tenant.key)}/policies/my`);
      await expect(page.getByTestId("my-policies").getByRole("link", { name: names[locale] })).toBeVisible();
      await page.goto(`${hostOrigin(tenant.key)}/tasks`);
      await expect(page.getByTestId("task-inbox").getByRole("link", { name: names[locale] })).toHaveCount(0);
    });
  });
}
