import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 021 T014 in the browser: the administrator opens locations and units from the workspace, picks their unit and
 * sees its dashboard say "not enough data" for a unit of one person. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`unit dashboards in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const names = { en: "Jeddah plant", ar: "مصنع جدة" };
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`sco-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "JED", names } });
      expect(created.status(), await created.text()).toBe(201);
      const unitId = ((await created.json()) as { unit: { id: string } }).unit.id;
      const me = (await (await api.get("/api/v1/me")).json()) as { id: string };
      expect((await api.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } })).ok()).toBe(true);
      await api.dispose();
    });

    test("an administrator opens a unit dashboard", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);
      await page.goto(`${origin}/analytics`);
      await page.getByRole("link", { name: m("analytics.workspace.scopesLink") }).click();
      await page.waitForURL(/\/analytics\/scopes$/);
      await expect(page.getByRole("heading", { level: 1, name: m("analytics.scopes.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `analytics-scopes-${locale}`);

      await page.getByRole("link", { name: names[locale] }).click();
      await page.waitForURL(/\/analytics\/scopes\/unit\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1, name: names[locale] })).toBeVisible();
      await expect(page.getByTestId("scope-dashboard")).toContainText(m("analytics.scopes.smallTitle"));
      await expect(page.getByTestId("scope-tile-adoption")).toContainText(m("analytics.scopes.notEnough"));
      await expectAccessible(page, testInfo, `analytics-scope-${locale}`);

      await page.goto(`${origin}/analytics/scopes/location`);
      await expect(page.getByText(m("analytics.scopes.pickTitle"))).toBeVisible();
    });
  });
}
