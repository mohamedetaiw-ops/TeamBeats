import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T017 (US8 scenario 1): a compliance officer opens Evidence packs from the register, picks a policy in force
 * and a period, asks for a pack, and once the background has built it downloads the CSV holding the policy's
 * versions and approval trail. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy evidence packs in ${locale}`, () => {
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
      tenant = await createTenant(`pe-${locale}`, locale, "enterprise_gov", ["rana"]);
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

    test("ask for an evidence pack and download it", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/policies`);
      await page.getByRole("link", { name: m("policies.nav.evidence") }).click();
      await page.waitForURL(/\/policies\/evidence$/);
      const form = page.getByTestId("evidence-form");
      await expect(form).toBeVisible();
      await expectAccessible(page, testInfo, `policy-evidence-${locale}`);

      const today = new Date();
      const iso = (d: Date) => d.toISOString().slice(0, 10);
      await form.getByRole("checkbox").first().check();
      await form.getByLabel(m("policies.evidence.from"), { exact: true }).fill(iso(new Date(today.getTime() - 30 * 86_400_000)));
      await form.getByLabel(m("policies.evidence.to"), { exact: true }).fill(iso(new Date(today.getTime() + 86_400_000)));
      await form.getByRole("button", { name: m("policies.evidence.ask") }).click();
      const row = page.getByTestId("evidence-pack");
      await expect(row).toHaveCount(1);

      // The background builds the pack; the page shows the files once it is ready.
      await expect(async () => {
        await page.reload();
        await expect(row.getByText(m("policies.evidence.statuses.ready"))).toBeVisible({ timeout: 2_000 });
      }).toPass({ timeout: 60_000 });
      const csv = row.getByRole("link", { name: m("policies.evidence.csv") });
      await expect(csv).toBeVisible();
      await expect(row.getByRole("link", { name: m("policies.evidence.pdf") })).toBeVisible();
      const [download] = await Promise.all([page.waitForEvent("download"), csv.click()]);
      const stream = await download.createReadStream();
      let text = "";
      for await (const chunk of stream) text += chunk.toString();
      expect(text).toContain("AUP-01");
      await expectAccessible(page, testInfo, `policy-evidence-ready-${locale}`);
    });
  });
}
