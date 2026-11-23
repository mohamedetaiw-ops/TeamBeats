import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T015 (US7): a person asks for an exemption to a policy in force with a reason and an end date, with the
 * keyboard; the owner and then compliance approve it in the inbox; the person sees it approved and the compliance
 * officer finds it in the exemption register. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy exemptions in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let policyId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const both = (en: string, ar: string) => ({ en, ar });
    const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });
    const reason = locale === "ar" ? "أعمل من موقع العميل دون الشبكة" : "Working from a client site without the VPN";

    async function ok(response: Awaited<ReturnType<APIRequestContext["post"]>>) {
      expect(response.ok(), `${response.url()}: ${await response.text()}`).toBe(true);
      return (await response.json()) as Record<string, unknown>;
    }

    async function approveAll(api: APIRequestContext) {
      // The owner's step, then the compliance step: the admin holds both here.
      for (let step = 0; step < 2; step++) {
        const tasks = (await (await api.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
        const task = tasks.find((t) => t.source === "policy_exemption");
        if (!task) return;
        await ok(await api.post(`/api/v1/tasks/${task.taskId}/decision`, { data: { outcome: "approve" } }));
      }
    }

    test.beforeAll(async () => {
      tenant = await createTenant(`px-${locale}`, locale, "workplace_plus", ["rana", "omar"]);
      const admin = await tenantApi(tenant);
      const policy = await ok(await admin.post("/api/v1/policies", { data: { code: "RA-01", names: both("Remote access", "الوصول عن بعد") } }));
      policyId = policy.id as string;
      await ok(await admin.put(`/api/v1/policies/${policyId}/draft`, {
        data: {
          titles: both("Remote access", "الوصول عن بعد"),
          clauses: [{ level: 1, titles: both("VPN", "الشبكة"), bodies: { en: body("Use the VPN."), ar: body("استخدم الشبكة.") } }],
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

    test("ask for an exemption, have it approved and find it in the register", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, `omar@${tenant.key}.example`, locale);

      // From the policy page to the exemption form.
      await page.goto(`${origin}/policies/${policyId}`);
      await page.getByRole("link", { name: m("policies.manage.exemptions") }).click();
      await page.waitForURL(/\/exemptions$/);
      const form = page.getByTestId("exemption-form");
      await expect(form).toBeVisible();
      await expectAccessible(page, testInfo, `policy-exemptions-${locale}`);

      // With the keyboard: the reason, the last day, then send.
      const end = new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10);
      await form.getByLabel(m("policies.exemptions.reason")).focus();
      await page.keyboard.type(reason);
      await form.getByLabel(m("policies.exemptions.endsOn")).fill(end);
      await form.getByRole("button", { name: m("policies.exemptions.submit") }).focus();
      await page.keyboard.press("Enter");
      await expect(page.getByTestId("exemption-asked")).toBeVisible();
      const row = page.getByTestId("exemption-row").first();
      await expect(row).toContainText(reason);
      await expect(row).toContainText(m("policies.exemptions.statuses.pending"));
      await expectAccessible(page, testInfo, `policy-exemption-asked-${locale}`);

      // The owner, then compliance, approve it in the inbox.
      const admin = await tenantApi(tenant);
      await approveAll(admin);
      await admin.dispose();
      await page.reload();
      await expect(page.getByTestId("exemption-row").first()).toContainText(m("policies.exemptions.statuses.approved"));

      // The compliance officer finds it in the exemption register.
      await page.context().clearCookies();
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/policies`);
      await page.getByRole("link", { name: m("policies.nav.exemptions") }).click();
      await page.waitForURL(/\/policies\/exemptions$/);
      await expect(page.getByTestId("exemption-row")).toHaveCount(1);
      await expect(page.getByTestId("exemption-row").first()).toContainText(reason);
      await expectAccessible(page, testInfo, `policy-exemption-register-${locale}`);
    });
  });
}
