import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { expectDetailConformant } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 015 US7 (R2): signage screens of a location. The administrator registers a screen at the warehouse unit and
 * gets its address once; a browser with nobody signed in opens it and shows the Public announcement marked for signage;
 * revoking the screen closes the address. Both screens pass axe in both languages. The feed rules (place, Public only,
 * withdrawal, replaced address, unit scope, tenant boundary, package) are covered by SignageScreenTests.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`signage screens in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    const m = (key: string, values?: Record<string, string>) => msg(locale, key, values);
    let tenant: TestTenant;
    const title = locale === "ar" ? "تدريب السلامة الساعة 10" : "Safety drill at 10";

    test.beforeAll(async () => {
      tenant = await createTenant(`signage-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const unit = await api.post("/api/v1/org/units", {
        data: { parentId: null, typeKey: "department", code: "WAREHOUSE", names: { en: "Warehouse", ar: "المستودع" } },
      });
      expect(unit.ok(), await unit.text()).toBe(true);
      const body = { blocks: [{ type: "paragraph", children: [{ text: title }] }] };
      const item = await api.post("/api/v1/content/items", {
        data: { type: "announcement", classification: "public", signage: true, variants: { [locale]: { title, body } } },
      });
      expect(item.ok(), await item.text()).toBe(true);
      const { id } = (await item.json()) as { id: string };
      const published = await api.post(`/api/v1/content/items/${id}/publish`, { data: {} });
      expect(published.ok(), await published.text()).toBe(true);
      await api.dispose();
    });

    test("a screen is registered, shows the announcement without signing in, and stops when revoked", async ({ page, browser }, testInfo) => {
      test.setTimeout(120_000);
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/admin/signage`);
      await expect(page.getByRole("heading", { level: 1, name: m("admin.nav.signage") })).toBeVisible();
      await expect(page.getByText(m("notifications.signage.emptyTitle"))).toBeVisible();
      await page.getByRole("textbox", { name: m("notifications.signage.name"), exact: true }).fill("Dock 3");
      await page.getByRole("button", { name: m("notifications.signage.create") }).click();
      const address = (await page.getByTestId("screen-address").textContent())?.trim() ?? "";
      expect(address).toContain("/screens/");
      await expect(page.getByRole("rowheader", { name: "Dock 3" })).toBeVisible();
      await expectAccessible(page, testInfo, `signage-admin-${locale}`);

      // The screen: a browser with nobody signed in.
      const display = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const screen = await display.newPage();
      await screen.goto(address);
      await expect(screen.getByRole("heading", { level: 1, name: "Dock 3" })).toBeVisible();
      await expect(screen.getByRole("heading", { level: 2, name: title })).toBeVisible();
      await expect(screen.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expectAccessible(screen, testInfo, `signage-screen-${locale}`);
      await expectDetailConformant(screen, testInfo, tenant, "/screens/[token]");

      await page.getByRole("button", { name: m("notifications.signage.revoke", { name: "Dock 3" }) }).click();
      await page.getByRole("button", { name: m("notifications.signage.revokeConfirm"), exact: true }).click();
      await expect(page.getByText(m("notifications.signage.emptyTitle"))).toBeVisible();
      await screen.reload();
      await expect(screen.getByRole("heading", { level: 2, name: title })).toHaveCount(0);
      await display.close();
    });
  });
}
