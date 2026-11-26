import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 R3 T026 (US9, US10): an approver opens the review of version 2, reads the facts of what the change touches
 * and asks for the AI impact assessment, labelled as AI-generated. The compliance officer opens Regulator changes from
 * the register and sees its state when no feed is connected yet; the items themselves are covered by the integration
 * tests, since the test stack has no regulator feed to read. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy change impact in ${locale}`, () => {
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
      tenant = await createTenant(`pim-${locale}`, locale, "enterprise_gov", ["rana"]);
      const admin = await tenantApi(tenant);
      const policy = await ok(await admin.post("/api/v1/policies", { data: { code: "AUP-02", names: both("Acceptable use", "الاستخدام المقبول") } }));
      policyId = policy.id as string;
      await ok(await admin.put(`/api/v1/policies/${policyId}/controls`, { data: { controls: [{ framework: "iso27001", code: "5.10" }] } }));
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

    test("read the facts of a change and the AI impact assessment", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, `rana@${tenant.key}.example`, locale);
      await page.goto(`${origin}/policies/${policyId}/review`);
      const panel = page.getByTestId("change-impact");
      await expect(panel).toBeVisible();
      await expect(panel).toContainText(m("policies.review.impact.facts.controls"));
      await expect(panel).toContainText("5.10");
      await expectAccessible(page, testInfo, `policy-impact-${locale}`);
      await panel.getByRole("button", { name: m("policies.review.impact.action") }).click();
      const assessment = panel.getByTestId("impact-assessment");
      await expect(assessment).toContainText(m("policies.review.impact.aiGenerated"));
      await expect(assessment.getByRole("list", { name: m("policies.review.impact.byClause") }).getByRole("listitem")).toHaveCount(2);
      await expect(panel.getByRole("button", { name: m("policies.review.impact.action") })).toHaveCount(0);
      await expectAccessible(page, testInfo, `policy-impact-done-${locale}`);
    });

    test("open Regulator changes from the register", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/policies`);
      await page.getByRole("link", { name: m("policies.nav.regulators"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1, name: m("policies.regulators.title") })).toBeVisible();
      await expect(page.getByText(m("policies.regulators.noFeeds"))).toBeVisible();
      await expect(page.getByText(m("policies.regulators.empty.new"))).toBeVisible();
      await expectAccessible(page, testInfo, `policy-regulators-${locale}`);
    });
  });
}
