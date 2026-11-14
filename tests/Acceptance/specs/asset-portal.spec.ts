import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "base64");

/**
 * Spec 011 R2 US5 in the browser: a brand manager creates a collection, adds an image with usage notes, sees its
 * renditions, downloads one and finds the download in the usage report. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`brand asset portal in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`ast-${locale}`, locale, "workplace_plus");
    });

    test("a brand manager adds an image and sees it downloaded", async ({ page }, testInfo) => {
      const collectionName = locale === "ar" ? "الشعارات" : "Logos";
      const assetName = locale === "ar" ? "الشعار الأساسي" : "Primary logo";

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/assets`);
      await expect(page.getByRole("heading", { level: 1, name: m("assets.title") })).toBeVisible();
      await expect(page.getByText(m("assets.noCollections"))).toBeVisible();

      await page.getByLabel(m("assets.collectionName")).fill(collectionName);
      await page.getByRole("button", { name: m("assets.createCollection") }).click();
      await expect(page.getByTestId("asset-collection").filter({ hasText: collectionName })).toBeVisible();
      await expect(page.getByText(m("assets.noAssets"))).toBeVisible();

      await page.locator("input[type=file]").setInputFiles({ name: "logo.png", mimeType: "image/png", buffer: png });
      await page.getByLabel(m("assets.assetTitle"), { exact: true }).fill(assetName);
      await page.getByLabel(m("assets.usageNotes"), { exact: true }).fill(locale === "ar" ? "على خلفية فاتحة فقط." : "On light backgrounds only.");
      await page.getByRole("button", { name: m("assets.uploadButton") }).click();
      const asset = page.getByTestId("asset").filter({ hasText: assetName });
      await expect(asset).toBeVisible();
      await expect(asset.getByText(m("assets.rightsOpen"))).toBeVisible();
      const renditions = asset.getByRole("list", { name: m("assets.downloadsFor", { name: assetName }) });
      await expect(renditions.getByRole("link", { name: m("assets.rendition.png_256") })).toBeVisible();
      // A one pixel source gets only the smallest PNG; it is never scaled up.
      await expect(renditions.getByRole("link", { name: m("assets.rendition.png_512") })).toHaveCount(0);
      await expect(renditions.getByRole("link", { name: m("assets.rendition.pdf") })).toBeVisible();
      await expectAccessible(page, testInfo, `asset-portal-${locale}`);

      const download = page.waitForEvent("download");
      await renditions.getByRole("link", { name: m("assets.rendition.png_256") }).click();
      expect((await download).suggestedFilename()).toContain("png_256");

      await page.getByRole("link", { name: m("assets.requestsLink") }).click();
      await expect(page.getByText(m("assets.noRequests"))).toBeVisible();
      await expect(page.getByTestId("download-total").filter({ hasText: assetName })).toBeVisible();
      await expectAccessible(page, testInfo, `asset-requests-${locale}`);
    });
  });
}
