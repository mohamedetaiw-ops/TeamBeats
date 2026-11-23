import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T016 (US8 scenario 3): a compliance officer maps a policy in force to ISO 27001 control 5.10 from the
 * control library, saves, and opens the control to see the policy listed with its status. In English and Arabic, with
 * axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy control mapping in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let policyId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const both = (en: string, ar: string) => ({ en, ar });
    const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });

    async function ok(response: Awaited<ReturnType<APIRequestContext["post"]>>) {
      expect(response.ok(), `${response.url()}: ${await response.text()}`).toBe(true);
      return (await response.json()) as Record<string, unknown>;
    }

    test.beforeAll(async () => {
      tenant = await createTenant(`pm-${locale}`, locale, "enterprise_gov", ["rana"]);
      const admin = await tenantApi(tenant);
      const policy = await ok(await admin.post("/api/v1/policies", { data: { code: "AUP-01", names: both("Acceptable use", "الاستخدام المقبول") } }));
      policyId = policy.id as string;
      await ok(await admin.put(`/api/v1/policies/${policyId}/draft`, {
        data: {
          titles: both("Acceptable use", "الاستخدام المقبول"),
          clauses: [{ level: 1, titles: both("Devices", "الأجهزة"), bodies: { en: body("Lock your screen."), ar: body("اقفل شاشتك.") } }],
        },
      }));
      await ok(await admin.post(`/api/v1/policies/${policyId}/submit`, { data: {} }));
      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      const tasks = (await (await rana.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
      const approval = tasks.find((t) => t.source === "policy_approval");
      expect(approval).toBeDefined();
      await ok(await rana.post(`/api/v1/tasks/${approval!.taskId}/decision`, { data: { outcome: "approve" } }));
      await rana.dispose();
      await admin.dispose();
    });

    test("map a policy to a control and see it on the control", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/policies/${policyId}`);
      await page.getByRole("link", { name: m("policies.manage.controls") }).click();
      await page.waitForURL(/\/controls$/);
      const editor = page.getByTestId("control-editor");
      await expect(editor).toBeVisible();
      await expectAccessible(page, testInfo, `policy-controls-${locale}`);

      // ISO 27001 is the first framework; choose control 5.10 with the keyboard, add it and save.
      await editor.getByLabel(m("policies.controls.control"), { exact: true }).selectOption("5.10");
      await editor.getByRole("button", { name: m("policies.controls.add") }).focus();
      await page.keyboard.press("Enter");
      await expect(editor.getByTestId("control-chosen").getByRole("listitem")).toHaveCount(1);
      await editor.getByRole("button", { name: m("policies.controls.save") }).click();
      await expect(editor.getByRole("status")).toHaveText(m("policies.controls.saved"));
      const mapped = page.getByTestId("mapped-controls").getByRole("link");
      await expect(mapped).toHaveCount(1);

      // The control lists the policy with its status.
      await mapped.first().click();
      await page.waitForURL(/\/policies\/controls\?framework=iso27001&code=5\.10$/);
      const table = page.getByTestId("control-policies");
      await expect(table.getByRole("link")).toHaveCount(1);
      await expect(table).toContainText("AUP-01");
      await expect(table).toContainText(m("policies.controls.statuses.published"));
      await expectAccessible(page, testInfo, `policy-control-policies-${locale}`);
    });
  });
}
