import { request } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 021 T013 in the browser: with a policy in force and an attestation campaign open, the administrator opens policy
 * compliance analytics from the workspace, sees the campaign with its small audience hidden, and follows the link to the
 * spec 025 attestation report. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy compliance analytics in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let policyId: string;
    const names = { en: "Code of conduct", ar: "مدونة السلوك" };
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`cpa-${locale}`, locale, "workplace_plus", ["legal"]);
      const host = `${tenant.key}.${stack.baseDomain}`;
      const legal = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
      expect((await legal.post("/dev/signin", { data: { email: `legal@${tenant.key}.example` } })).ok()).toBe(true);
      const legalId = ((await (await legal.get("/api/v1/me")).json()) as { id: string }).id;
      const admin = await tenantApi(tenant);
      const created = await admin.post("/api/v1/policies", { data: { code: "POL-001", names, route: [{ approvers: [legalId] }] } });
      expect(created.status(), await created.text()).toBe(201);
      policyId = ((await created.json()) as { id: string }).id;
      const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });
      const draft = await admin.put(`/api/v1/policies/${policyId}/draft`, {
        data: { titles: names, clauses: [{ level: 1, titles: names, bodies: { en: body("Conduct applies to everyone."), ar: body("ينطبق السلوك على الجميع.") } }], material: true },
      });
      expect(draft.ok(), await draft.text()).toBe(true);
      expect((await admin.post(`/api/v1/policies/${policyId}/submit`, { data: {} })).ok()).toBe(true);
      const tasks = (await (await legal.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
      const task = tasks.find((t) => t.source === "policy_approval");
      expect(task).toBeTruthy();
      expect((await legal.post(`/api/v1/tasks/${task!.taskId}/decision`, { data: { outcome: "approve" } })).ok()).toBe(true);
      await legal.dispose();
      expect((await admin.post(`/api/v1/policies/${policyId}/campaigns`, { data: {} })).status()).toBe(201);
      await admin.dispose();
    });

    test("a compliance officer follows open campaigns", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);
      await page.goto(`${origin}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.complianceLink") }).click();
      await page.waitForURL(/\/analytics\/compliance$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.compliance.title") })).toBeVisible();
      const campaign = page.getByTestId("compliance-campaign");
      await expect(campaign.getByRole("heading", { level: 2 })).toContainText(names[locale]);
      // Two people are under the minimum group size: the rate is hidden and no groups are shown.
      await expect(page.getByTestId("compliance-total")).toContainText(m("analytics.compliance.hidden", { size: "5" }));
      await expect(campaign.getByTestId("compliance-units")).toContainText(m("analytics.compliance.tooFew", { size: "5" }));
      await expectAccessible(page, testInfo, `analytics-compliance-${locale}`);

      await campaign.getByRole("link", { name: m("analytics.compliance.report") }).click();
      await page.waitForURL(new RegExp(`/policies/${policyId}/compliance\\?campaign=`));
    });
  });
}
