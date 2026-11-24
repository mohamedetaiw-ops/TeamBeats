import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, platformPut } from "../support/platform";
import { hostOrigin } from "../support/stack";

const day = 24 * 60 * 60 * 1000;

/**
 * The plan page in R2 (spec 005 T014 extended): add-ons and contract terms with their dates, this month's use of each
 * metered quota, and the contract end with its grace or read-only state. The platform operator sets the contract
 * through the API; the page only shows what the API returns.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`plan page in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    test(`the plan page shows add-ons, quota use and the contract state in ${locale}`, async ({ page }, testInfo) => {
      const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);
      const tenant = await createTenant(`plan-${locale}`, locale);
      const base = `/api/v1/platform/tenants/${tenant.id}`;
      await platformPut(`${base}/addons/authz.custom_roles`, { enabled: true, reason: "Bought with the HR bundle" });
      await platformPut(`${base}/overrides/ai.assistant.monthly_requests`, {
        enabled: true,
        limit: 500,
        validTo: new Date(Date.now() + 90 * day).toISOString(),
        reason: "Pilot terms",
      });
      await platformPut(`${base}/contract`, { endsAt: new Date(Date.now() - 2 * day).toISOString(), graceDays: 30 });

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/plan`);
      const main = page.locator("#main");
      await expect(main.getByText(m("admin.plan.contract.graceTitle"))).toBeVisible();
      await expect(main.getByText(m("admin.plan.contract.ends"))).toBeVisible();

      const grants = page.getByRole("region", { name: m("admin.plan.grants.title") });
      await expect(grants.getByText(m("admin.plan.capabilities.authz_custom_roles"), { exact: true })).toBeVisible();
      await expect(grants.getByText(m("admin.plan.grants.kinds.addon"), { exact: true })).toBeVisible();
      await expect(grants.getByText(m("admin.plan.grants.kinds.override"), { exact: true })).toBeVisible();
      await expect(grants.getByText(m("admin.plan.grants.active"), { exact: true })).toHaveCount(2);

      const usage = page.getByRole("region", { name: m("admin.plan.usage.title") });
      await expect(usage.getByRole("meter", { name: m("admin.plan.capabilities.ai_assistant_monthly_requests") })).toHaveAttribute("aria-valuemax", "500");
      await expectAccessible(page, testInfo, `admin-plan-contract-${locale}`);

      // After the grace period the workspace is read only, and the page says so.
      await platformPut(`${base}/contract`, { endsAt: new Date(Date.now() - 60 * day).toISOString(), graceDays: 0 });
      await page.reload();
      await expect(main.getByText(m("admin.plan.contract.readOnlyTitle"))).toBeVisible();
    });
  });
}
