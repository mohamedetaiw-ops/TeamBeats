import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 R2 T022 (US8): from the brand manager an administrator opens theme governance, imports a token file into the
 * brand's draft and proposes a campaign accent; a second administrator approves it, and the theme then serves it. In
 * English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`theme governance in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let brandName: string;
    const reviewer = () => `rana@${tenant.key}.example`;

    test.beforeAll(async () => {
      tenant = await createTenant(`tgv-${locale}`, locale, "enterprise_gov", ["rana"]);
      // The tenant's brand is made on first use, and the second administrator signs in once so their account is there.
      const api = await tenantApi(tenant);
      const brands = await api.get("/api/v1/experience/brands");
      expect(brands.ok()).toBe(true);
      brandName = ((await brands.json()) as { name: string }[])[0]!.name;
      await api.dispose();
      await (await tenantApiAs(tenant, reviewer())).dispose();
    });

    test("an administrator imports tokens and proposes an extension that another approves", async ({ page }, testInfo) => {
      const g = (key: string, values: Record<string, string> = {}) => msg(locale, `brand.governance.${key}`, values);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding`);
      await page.getByRole("link", { name: g("open"), exact: true }).click();
      await expect(page).toHaveURL(/\/admin\/branding\/governance/);
      await expect(page.getByRole("heading", { name: g("driftTitle"), exact: true })).toBeVisible();
      await expect(page.getByRole("link", { name: g("export"), exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "governance");

      // A token file from another tool goes into the draft.
      const file = { color: { accent: { base: { $type: "color", $value: { colorSpace: "srgb", components: [0, 0.4, 0.8] } } } } };
      await expect(async () => {
        await page.locator("input[type=file]").setInputFiles({ name: "brand.tokens.json", mimeType: "application/json", buffer: Buffer.from(JSON.stringify(file)) });
        await expect(page.getByRole("button", { name: g("import"), exact: true })).toBeEnabled({ timeout: 1_000 });
      }).toPass();
      await page.getByRole("button", { name: g("import"), exact: true }).click();
      await expect(page.getByText(g("imported", { brand: brandName }), { exact: true })).toBeVisible();

      // A campaign accent for the whole workspace waits for review.
      const add = page.getByRole("button", { name: g("propose"), exact: true });
      await expect(async () => {
        await page.getByLabel(g("key"), { exact: true }).fill("campaign_accent");
        await page.getByLabel(g("value"), { exact: true }).fill("#be185d");
        await expect(add).toBeEnabled({ timeout: 1_000 });
      }).toPass();
      await add.click();
      const table = page.getByRole("table").filter({ hasText: "--tb-ext-campaign-accent" });
      await expect(table).toContainText(g("states.proposed"));
      // The person who proposed it is refused when approving it.
      await table.getByRole("button", { name: g("approveFor", { key: "campaign_accent" }), exact: true }).click();
      await expect(page.getByText(msg(locale, "admin.common.notDone"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "governance-proposed");

      // Another administrator approves it, and the theme serves it.
      await page.context().clearCookies();
      await signInWithDevForm(page, tenant.key, reviewer(), locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding/governance`);
      const again = page.getByRole("table").filter({ hasText: "--tb-ext-campaign-accent" });
      await again.getByRole("button", { name: g("approveFor", { key: "campaign_accent" }), exact: true }).click();
      await expect(again).toContainText(g("states.approved"));
      // Read in the browser, which resolves the tenant's host name.
      const css = await page.evaluate(async () =>
        (await fetch("/api/v1/experience/theme.css?scope=tenant&mode=light&density=comfortable", { cache: "no-store" })).text(),
      );
      expect(css).toContain("--tb-ext-campaign-accent:#be185d;");
      await expectAccessible(page, testInfo, "governance-approved");
    });
  });
}
