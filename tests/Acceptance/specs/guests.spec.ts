import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 026 R2 slice 2: a guest signs in to the reduced guest shell and finds the space they were invited to, with the
 * person who invited them and the end date; then the administrator opens an access review and removes the guest, in
 * English and Arabic with axe. The API decides every rule; the pages only show its answers.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`guests and access reviews in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let guest: string;
    const space = locale === "ar" ? "مشروع المورد" : "Vendor rollout";

    test.beforeAll(async () => {
      tenant = await createTenant(`guest-${locale}`, locale, "workplace_plus");
      guest = `rania@partner-${tenant.key}.example`;
      const api = await tenantApi(tenant);
      const rules = await api.get("/api/v1/spaces/manage/settings");
      expect(rules.ok()).toBe(true);
      const version = ((await rules.json()) as { version: number }).version;
      expect((await api.put("/api/v1/spaces/manage/settings", { data: { creationUnits: [], guestsAllowed: true, guestDays: 60, version } })).ok()).toBe(true);
      const created = await api.post("/api/v1/spaces", { data: { name: space, description: space, visibility: "private", template: "project" } });
      expect(created.status()).toBe(201);
      const id = ((await created.json()) as { id: string }).id;
      expect((await api.post(`/api/v1/spaces/${id}/guests`, { data: { email: guest, days: 30 } })).ok()).toBe(true);
      await api.dispose();
    });

    test("a guest gets the reduced shell and a home that lists their space", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, guest, locale);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "spaces.guestHome.title"));
      const card = page.getByRole("list", { name: msg(locale, "spaces.guestHome.listLabel") }).getByRole("listitem");
      await expect(card.getByRole("link", { name: space })).toBeVisible();
      await expect(card.getByText(msg(locale, "spaces.guestHome.sponsor"))).toBeVisible();
      // The shell says the person is a guest and has no navigation rail, search or assistant.
      await expect(page.locator("header.shell").getByText(msg(locale, "shell.guest"), { exact: true })).toBeVisible();
      await expect(page.getByRole("navigation", { name: msg(locale, "shell.globalNav") })).toHaveCount(0);
      await expect(page.getByRole("link", { name: msg(locale, "shell.ask") })).toHaveCount(0);
      await expectAccessible(page, testInfo, `guest-home-${locale}`);
    });

    test("the administrator opens an access review and removes the guest", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/security/reviews`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "security.reviews.title"));
      await page.getByRole("button", { name: msg(locale, "security.reviews.start") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "security.reviews.started") })).toBeVisible();
      await expectAccessible(page, testInfo, `security-reviews-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/access-reviews`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "accessReviews.title"));
      const row = page.getByRole("row").filter({ hasText: space });
      await expect(row).toBeVisible();
      await expectAccessible(page, testInfo, `access-reviews-${locale}`);
      await row.getByRole("button", { name: msg(locale, "accessReviews.remove") }).click();
      await page.getByRole("alertdialog").getByRole("button", { name: msg(locale, "accessReviews.remove") }).click();
      await expect(row.getByText(msg(locale, "accessReviews.decisions.removed"))).toBeVisible();
    });
  });
}
