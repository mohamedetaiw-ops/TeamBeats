import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";
import { zip } from "../support/zip";

/**
 * Spec 011 R2 FR-022 in the browser: a library manager uploads a ZIP with a manifest, watches the import finish and
 * reads each row's result; a refused manifest says why. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`document import in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);

    test.beforeAll(async () => {
      tenant = await createTenant(`imp-${locale}`, locale, "workplace_plus");
    });

    test("a library manager imports a share and reads the report", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const libraryName = locale === "ar" ? "الأرشيف" : "Archive";
      const library = await api.post("/api/v1/documents/libraries", { data: { names: { [locale]: libraryName }, scope: "tenant" } });
      expect(library.status()).toBe(201);
      const libraryId = ((await library.json()) as { id: string }).id;

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/documents/libraries/${libraryId}`);
      await page.getByRole("link", { name: m("documents.libraries.importLink") }).click();
      await expect(page.getByRole("heading", { level: 1, name: m("documents.imports.title") })).toBeVisible();
      await expect(page.getByText(m("documents.imports.none"))).toBeVisible();
      await expectAccessible(page, testInfo, `document-import-${locale}`);

      // A ZIP without a manifest is refused with the reason.
      await page.locator("input[type=file]").setInputFiles({ name: "empty.zip", mimeType: "application/zip", buffer: zip({ "a.txt": "x" }) });
      await page.getByRole("button", { name: m("documents.imports.start") }).click();
      await expect(page.getByRole("main").locator(".notice-error")).toBeVisible();

      const manifest = ["path,folder,created", "policies/leave.txt,Policies,2019-03-01", "policies/travel.txt,Policies,", "missing.txt,,"].join("\n");
      const archive = zip({ "manifest.csv": manifest, "policies/leave.txt": "Leave rules", "policies/travel.txt": "Travel rules" });
      await page.locator("input[type=file]").setInputFiles({ name: "share.zip", mimeType: "application/zip", buffer: archive });
      await page.getByRole("button", { name: m("documents.imports.start") }).click();
      await page.waitForURL(/\/documents\/imports\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 2, name: m("documents.imports.status.completed") })).toBeVisible({ timeout: 30_000 });
      await expect(page.getByTestId("count-imported")).toContainText("2");
      await expect(page.getByTestId("count-failed")).toContainText("1");
      await expect(page.getByTestId("import-row")).toHaveCount(3);
      await expectAccessible(page, testInfo, `document-import-report-${locale}`);

      await page.getByTestId("count-failed").click();
      await expect(page.getByTestId("import-row")).toHaveCount(1);
      await expect(page.getByTestId("import-row")).toContainText("missing.txt");

      const download = page.waitForEvent("download");
      await page.getByRole("link", { name: m("documents.imports.downloadReport") }).click();
      expect((await download).suggestedFilename()).toMatch(/^import-[0-9a-f]{32}\.csv$/);
    });
  });
}
