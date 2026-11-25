import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { expectDetailConformant } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T020 (R2 manager dashboards): a line manager opens My team from the register and sees the open campaign with
 * their report who still owes it. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy team dashboard in ${locale}`, () => {
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
      tenant = await createTenant(`ptm-${locale}`, locale, "enterprise_gov", ["rana"]);
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
      const ranaId = ((await (await rana.get("/api/v1/me")).json()) as { id: string }).id;
      const adminId = ((await (await admin.get("/api/v1/me")).json()) as { id: string }).id;
      await ok(await admin.put(`/api/v1/people/${ranaId}/manager`, { data: { managerId: adminId } }));
      await ok(await admin.post(`/api/v1/policies/${policyId}/campaigns`, { data: {} }));
      await rana.dispose();
      await admin.dispose();
    });

    test("see the team's open campaigns from the register", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/policies`);
      await page.getByRole("link", { name: m("policies.nav.team") }).click();
      await page.waitForURL(/\/policies\/team$/);
      const card = page.getByTestId("team-policy");
      await expect(card).toHaveCount(1);
      await expect(card.getByRole("table", { name: m("policies.team.owing") }).getByRole("row").filter({ hasText: /rana/i })).toHaveCount(1);
      await expectAccessible(page, testInfo, `policy-team-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/policies/team");
    });
  });
}
