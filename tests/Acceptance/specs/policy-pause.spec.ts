import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T020 (edge cases, long leave): a compliance officer opens the compliance view of a policy with an open
 * campaign, pauses one person's deadline with the day they are back and a reason, and sees the pause on their row.
 * In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy deadline pause in ${locale}`, () => {
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
      tenant = await createTenant(`ps-${locale}`, locale, "enterprise_gov", ["rana"]);
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
      await ok(await admin.post(`/api/v1/policies/${policyId}/campaigns`, { data: {} }));
      await admin.dispose();
    });

    test("pause a deadline for long leave", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/policies/${policyId}/compliance`);
      const row = page.getByTestId("compliance-people").getByRole("row").filter({ hasText: /rana/i });
      await expect(row).toHaveCount(1);
      await row.getByTestId("pause-deadline").click();
      const dialog = page.getByRole("dialog");
      await expect(dialog).toBeVisible();
      await expectAccessible(page, testInfo, `policy-pause-${locale}`);

      const back = new Date(Date.now() + 40 * 86_400_000).toISOString().slice(0, 10);
      await dialog.getByLabel(m("policies.compliance.pauseUntil")).fill(back);
      await dialog.getByLabel(m("policies.compliance.pauseReason")).fill("Parental leave");
      await dialog.getByRole("button", { name: m("policies.compliance.pauseSubmit") }).click();
      await expect(dialog).toBeHidden();
      await expect(row).toContainText(m("policies.compliance.pausedUntil", { date: "" }).trim());
      await expect(row.getByTestId("pause-deadline")).toHaveCount(0);
      await expect(page.getByText("Parental leave")).toHaveCount(0);
      await expectAccessible(page, testInfo, `policy-pause-done-${locale}`);
    });
  });
}
