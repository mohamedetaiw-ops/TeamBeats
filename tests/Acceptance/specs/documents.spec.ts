import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 011 T013 and T014: the administrator opens a library, uploads a text file with the upload dialog, finds it with
 * the search, opens the document page, previews it, checks it out and checks it in as a major version, and sees both
 * versions. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`documents in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const libraryName = locale === "ar" ? "سياسات الموارد البشرية" : "HR policies";
    const fileText = locale === "ar" ? "يحصل الموظفون على ثلاثين يومًا من الإجازة." : "Staff get thirty days of leave.";

    test.beforeAll(async () => {
      tenant = await createTenant(`doc-${locale}`, locale, "workplace");
      const api = await tenantApi(tenant);
      const created = await api.post("/api/v1/documents/libraries", { data: { names: { [locale]: libraryName }, scope: "tenant" } });
      expect(created.status()).toBe(201);
      await api.dispose();
    });

    test("upload, find, preview, check out and check in a document", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/documents`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "documents.libraries.title"));
      await expectAccessible(page, testInfo, `documents-libraries-${locale}`);
      await page.getByTestId("library-list").getByRole("link", { name: libraryName }).click();
      await page.waitForURL(/\/documents\/libraries\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(libraryName);
      await expect(page.getByText(msg(locale, "documents.browser.none"))).toBeVisible();
      await expectAccessible(page, testInfo, `documents-library-${locale}`);

      // Upload through the dialog.
      await page.getByRole("button", { name: msg(locale, "documents.upload.open") }).click();
      const dialog = page.getByRole("dialog", { name: msg(locale, "documents.upload.title") });
      await expect(dialog).toBeVisible();
      await expectAccessible(page, testInfo, `documents-upload-${locale}`);
      await dialog.getByLabel(msg(locale, "documents.upload.file")).setInputFiles({ name: "leave-policy.txt", mimeType: "text/plain", buffer: Buffer.from(fileText) });
      await dialog.getByRole("button", { name: msg(locale, "documents.upload.submit") }).click();
      await expect(dialog).toBeHidden();
      await expect(page.getByRole("status").filter({ hasText: "leave-policy.txt" })).toBeVisible();
      await expect(page.getByRole("link", { name: "leave-policy.txt" })).toBeVisible();

      // The search narrows the list.
      await page.getByLabel(msg(locale, "documents.browser.search")).fill("nothing-like-this");
      await page.getByRole("button", { name: msg(locale, "documents.browser.apply") }).click();
      await expect(page.getByText(msg(locale, "documents.browser.none"))).toBeVisible();
      await page.getByLabel(msg(locale, "documents.browser.search")).fill("leave");
      await page.getByRole("button", { name: msg(locale, "documents.browser.apply") }).click();
      await page.getByRole("link", { name: "leave-policy.txt" }).click();
      await page.waitForURL(/\/documents\/[0-9a-f-]{36}$/);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText("leave-policy.txt");
      await expect(page.getByTestId("document-version")).toHaveText("1.0");

      await page.getByRole("button", { name: msg(locale, "documents.document.preview") }).click();
      await expect(page.locator(".documents-preview pre")).toHaveText(fileText);
      await expectAccessible(page, testInfo, `documents-document-${locale}`);

      // Check out, then check in a new file as the next major version.
      await page.getByRole("button", { name: msg(locale, "documents.document.checkOut") }).click();
      await expect(page.getByRole("main").getByRole("status")).toContainText(msg(locale, "documents.document.checkedOutDone"));
      await expect(page.getByText(msg(locale, "documents.document.checkedOutByYou"))).toBeVisible();
      await page.getByLabel(msg(locale, "documents.document.newFile")).setInputFiles({ name: "leave-policy.txt", mimeType: "text/plain", buffer: Buffer.from(`${fileText} 2`) });
      await page.getByLabel(msg(locale, "documents.document.note")).fill(locale === "ar" ? "تحديث المدة" : "Updated the period");
      await page.getByLabel(msg(locale, "documents.document.major")).check();
      await expectAccessible(page, testInfo, `documents-checkin-${locale}`);
      await page.getByRole("button", { name: msg(locale, "documents.document.checkIn"), exact: true }).click();
      await expect(page.getByRole("main").getByRole("status")).toContainText(msg(locale, "documents.document.checkedInDone"));
      await expect(page.getByTestId("document-version")).toHaveText("2.0");
      await expect(page.getByTestId("version-list").locator("tbody tr")).toHaveCount(2);
      await expect(page.getByRole("button", { name: msg(locale, "documents.document.restore", { version: "1.0" }) })).toBeVisible();
    });
  });
}
