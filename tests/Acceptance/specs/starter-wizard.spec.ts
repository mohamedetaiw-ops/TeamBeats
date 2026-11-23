import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 R2 T020 (US6, FR-027): an administrator runs the starter wizard for a new organization, picks a design,
 * navigation, language and a starter page, gets a draft that people there do not see, and publishes it. In English and
 * Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`starter wizard in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let unitId: string;
    const gulf = { en: "Acme Gulf", ar: "أكمي الخليج" };

    test.beforeAll(async () => {
      tenant = await createTenant(`stw-${locale}`, locale, "workplace_plus", ["lead"]);
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/org/units", { data: { parentId: null, typeKey: "department", code: "GLF", names: gulf } });
      expect(created.ok(), await created.text()).toBe(true);
      unitId = ((await created.json()) as { unit: { id: string } }).unit.id;
      const lead = await tenantApiAs(tenant, `lead@${tenant.key}.example`);
      const me = (await (await lead.get("/api/v1/me")).json()) as { id: string };
      await lead.dispose();
      expect((await api.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } })).ok()).toBe(true);
      await api.dispose();
    });

    test("an administrator sets up a new organization as a draft and publishes it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/organization`);
      await page.getByRole("link", { name: msg(locale, "admin.org.starter.open"), exact: true }).click();
      await expect(page).toHaveURL(/\/admin\/organization\/starter$/);

      const formTitle = page.getByRole("heading", { name: msg(locale, "admin.org.starter.formTitle"), exact: true });
      // The organization is chosen again until the screen has hydrated and reads its starter.
      await expect(async () => {
        await page.getByLabel(msg(locale, "admin.org.starter.organization"), { exact: true }).selectOption({ label: gulf[locale] });
        await expect(formTitle).toBeVisible({ timeout: 2_000 });
      }).toPass();
      // Card choices draw their own control over the native one, so a person clicks the card's label.
      await page.getByRole("group", { name: msg(locale, "admin.org.starter.design"), exact: true }).getByText(msg(locale, "admin.org.starter.designs.atlas"), { exact: true }).click();
      await expect(page.getByRole("radio", { name: msg(locale, "admin.org.starter.designs.atlas") })).toBeChecked();
      await page.getByRole("radio", { name: msg(locale, "admin.org.starter.navigationPatterns.sidebar"), exact: true }).check();
      const pages = page.getByRole("group", { name: msg(locale, "admin.org.starter.pages"), exact: true });
      await pages.locator(".ui-choice-text").first().click();
      await expectAccessible(page, testInfo, "starter-form");
      await page.getByRole("button", { name: msg(locale, "admin.org.starter.create"), exact: true }).click();

      const made = page.getByTestId("starter-made");
      await expect(made).toContainText(msg(locale, "admin.org.starter.madeDraft"));
      await expect(made.getByRole("table")).toContainText(msg(locale, "admin.org.starter.pageDraft"));
      await expectAccessible(page, testInfo, "starter-draft");

      await made.getByRole("button", { name: msg(locale, "admin.org.starter.publish"), exact: true }).click();
      await expect(made).toContainText(msg(locale, "admin.org.starter.madePublished"));
      await expect(made.getByRole("table")).toContainText(msg(locale, "admin.org.starter.pagePublished"));
    });
  });
}
