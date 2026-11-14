import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 011 R2 US8 in the browser: a library manager makes a library view only, allows guest sharing, shares a folder
 * with a space they own and ends the share; the space's files page lists the shared folder. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`document sharing in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`shr-${locale}`, locale, "workplace_plus");
    });

    test("a library manager limits downloads and shares a folder with a space", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const libraryName = locale === "ar" ? "أدلة التدقيق" : "Audit evidence";
      const library = await api.post("/api/v1/documents/libraries", { data: { names: { [locale]: libraryName }, scope: "tenant" } });
      expect(library.status()).toBe(201);
      const libraryId = ((await library.json()) as { id: string }).id;
      const folderName = locale === "ar" ? "الأدلة" : "Evidence";
      expect((await api.post(`/api/v1/documents/libraries/${libraryId}/folders`, { data: { name: folderName } })).status()).toBe(201);
      const spaceName = locale === "ar" ? "تدقيق خارجي" : "External audit";
      const space = await api.post("/api/v1/spaces", { data: { name: spaceName, description: "Audit.", visibility: "private", template: "project" } });
      expect(space.status()).toBe(201);
      const spaceId = ((await space.json()) as { id: string }).id;

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/documents/libraries/${libraryId}`);
      await expect(page.getByRole("heading", { level: 2, name: m("documents.sharing.title") })).toBeVisible();

      await page.getByLabel(m("documents.sharing.viewOnly")).click();
      await expect(page.getByText(m("documents.sharing.settingsSaved"))).toBeVisible();
      await expect(page.getByLabel(m("documents.sharing.viewOnly"))).toBeChecked();
      await page.getByLabel(m("documents.sharing.guestSharing")).click();
      await expect(page.getByLabel(m("documents.sharing.guestSharing"))).toBeChecked();

      await page.getByLabel(m("documents.sharing.folder"), { exact: true }).selectOption({ label: folderName });
      await page.getByLabel(m("documents.sharing.space"), { exact: true }).selectOption({ label: spaceName });
      await page.getByLabel(m("documents.sharing.days"), { exact: true }).fill("30");
      await page.getByRole("button", { name: m("documents.sharing.share") }).click();
      const share = page.getByTestId("guest-share").filter({ hasText: folderName });
      await expect(share).toBeVisible();
      await expectAccessible(page, testInfo, `document-sharing-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/spaces/${spaceId}/files`);
      await expect(page.getByRole("heading", { level: 2, name: m("spaces.sharedFolders") })).toBeVisible();
      await expect(page.getByTestId("shared-folder").getByRole("link", { name: folderName })).toBeVisible();

      await page.goto(`${hostOrigin(tenant.key)}/documents/libraries/${libraryId}`);
      await share.getByRole("button", { name: m("documents.sharing.revokeNamed", { folder: folderName, space: spaceName }) }).click();
      await expect(share.getByText(m("documents.sharing.revokedBadge"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `document-sharing-revoked-${locale}`);
    });
  });
}
