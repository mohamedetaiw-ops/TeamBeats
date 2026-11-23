import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T020 (section 11): an approver opens the review of version 2, asks for the AI change summary beside the
 * compare view and reads it labelled as AI-generated. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy change summary in ${locale}`, () => {
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
      tenant = await createTenant(`pcs-${locale}`, locale, "enterprise_gov", ["rana"]);
      const admin = await tenantApi(tenant);
      const policy = await ok(await admin.post("/api/v1/policies", { data: { code: "AUP-01", names: both("Acceptable use", "الاستخدام المقبول") } }));
      policyId = policy.id as string;
      const first = await ok(await admin.put(`/api/v1/policies/${policyId}/draft`, {
        data: {
          titles: both("Acceptable use", "الاستخدام المقبول"),
          clauses: [{ level: 1, titles: both("Devices", "الأجهزة"), bodies: { en: body("Lock your screen."), ar: body("اقفل شاشتك.") } }],
        },
      }));
      const key = (first.clauses as { key: string }[])[0].key;
      await ok(await admin.post(`/api/v1/policies/${policyId}/submit`, { data: {} }));
      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      const tasks = (await (await rana.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
      const approval = tasks.find((t) => t.source === "policy_approval");
      expect(approval).toBeDefined();
      await ok(await rana.post(`/api/v1/tasks/${approval!.taskId}/decision`, { data: { outcome: "approve" } }));
      await ok(await admin.put(`/api/v1/policies/${policyId}/draft`, {
        data: {
          titles: both("Acceptable use", "الاستخدام المقبول"),
          clauses: [
            { key, level: 1, titles: both("Devices", "الأجهزة"), bodies: { en: body("Lock your screen when you leave."), ar: body("اقفل شاشتك عند المغادرة.") } },
            { level: 1, titles: both("Email", "البريد"), bodies: { en: body("Do not forward work email."), ar: body("لا تعد توجيه بريد العمل.") } },
          ],
        },
      }));
      await ok(await admin.post(`/api/v1/policies/${policyId}/submit`, { data: {} }));
      await rana.dispose();
      await admin.dispose();
    });

    test("read the AI summary of the changes beside the compare view", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, `rana@${tenant.key}.example`, locale);
      await page.goto(`${origin}/policies/${policyId}/review`);
      const panel = page.getByTestId("change-summary");
      await expect(panel).toBeVisible();
      await expectAccessible(page, testInfo, `policy-change-summary-${locale}`);
      await panel.getByRole("button", { name: m("policies.review.summary.action") }).click();
      await expect(panel).toContainText(m("policies.review.summary.aiGenerated"));
      await expect(panel.getByRole("list", { name: m("policies.review.summary.points") }).getByRole("listitem")).toHaveCount(2);
      await expect(panel.getByRole("button", { name: m("policies.review.summary.action") })).toHaveCount(0);
      await expectAccessible(page, testInfo, `policy-change-summary-done-${locale}`);
    });
  });
}
