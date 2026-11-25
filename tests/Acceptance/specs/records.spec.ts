import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 011 R2 US6 and US7 in the browser: a records manager creates a retention label, applies it to a library and
 * places a legal hold on that library, then sees the hold and the document it covers. Spec 011 R3: a records manager
 * declares a document a record on its page, finds it on the records page, and is told why an empty range gives no
 * disposition certificate; the library page tells its manager that no customer-managed key is connected. In English and Arabic, with axe.
 */
function apiText(locale: LaunchLocale, key: string): string {
  const file = fileURLToPath(new URL(`../../../src/BuildingBlocks/Localization/catalogues/${locale}.json`, import.meta.url));
  const value = (JSON.parse(readFileSync(file, "utf8")) as Record<string, string>)[key];
  if (!value) throw new Error(`No API text for ${key}`);
  return value;
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`records management in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`rec-${locale}`, locale, "enterprise_gov");
    });

    test("a records manager labels a library and holds it", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const libraryName = locale === "ar" ? "سجلات الجودة" : "Quality records";
      const library = await api.post("/api/v1/documents/libraries", { data: { names: { [locale]: libraryName }, scope: "tenant" } });
      expect(library.status()).toBe(201);
      const libraryId = ((await library.json()) as { id: string }).id;
      const file = await api.post(`/api/v1/documents/libraries/${libraryId}/files?name=audit-plan.txt&publish=true`, {
        headers: { "content-type": "application/octet-stream" },
        data: Buffer.from("Audit plan"),
      });
      expect(file.status()).toBe(201);

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/documents`);
      await page.getByRole("link", { name: m("documents.libraries.recordsLink") }).click();
      await expect(page.getByRole("heading", { level: 1, name: m("records.title") })).toBeVisible();
      await expectAccessible(page, testInfo, `records-${locale}`);

      const labelName = locale === "ar" ? "سجل جودة" : "Quality record";
      await page.getByLabel(m(locale === "ar" ? "records.labels.nameAr" : "records.labels.nameEn")).fill(labelName);
      if (locale === "ar") await page.getByLabel(m("records.labels.nameEn")).fill("Quality record");
      await page.getByLabel(m("records.labels.months")).fill("120");
      await page.getByRole("button", { name: m("records.labels.create") }).click();
      await expect(page.getByText(m("records.labels.saved"))).toBeVisible();
      await expect(page.getByTestId("retention-label").filter({ hasText: labelName })).toBeVisible();

      const select = page.getByLabel(m("records.apply.library", { name: libraryName }));
      await select.selectOption({ label: labelName });
      await expect(page.getByTestId("retention-label").filter({ hasText: labelName })).toContainText(locale === "ar" ? "مستند واحد" : "1 document");

      const holdName = locale === "ar" ? "نزاع المورد" : "Supplier dispute";
      await page.getByLabel(m("records.holds.name"), { exact: true }).fill(holdName);
      await page.getByLabel(m("records.holds.reason")).fill(locale === "ar" ? "تجميد للتقاضي" : "Litigation hold");
      await page.getByLabel(m("records.holds.target")).selectOption("library");
      await page.getByLabel(m("records.holds.library"), { exact: true }).selectOption({ label: libraryName });
      await page.getByRole("button", { name: m("records.holds.create") }).click();
      const hold = page.getByTestId("legal-hold").filter({ hasText: holdName });
      await expect(hold).toBeVisible();
      await hold.getByRole("button", { name: m("records.holds.showDocuments", { name: holdName }) }).click();
      await expect(hold.getByRole("link", { name: "audit-plan.txt" })).toBeVisible();
      await expectAccessible(page, testInfo, `records-hold-${locale}`);
    });

    test("a records manager declares a record and checks the certificate range", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const library = await api.post("/api/v1/documents/libraries", { data: { names: { [locale]: locale === "ar" ? "العقود" : "Contracts" }, scope: "tenant" } });
      expect(library.status()).toBe(201);
      const libraryId = ((await library.json()) as { id: string }).id;
      const file = await api.post(`/api/v1/documents/libraries/${libraryId}/files?name=signed-contract.txt&publish=true`, {
        headers: { "content-type": "application/octet-stream" },
        data: Buffer.from("Signed contract"),
      });
      expect(file.status()).toBe(201);
      const documentId = ((await file.json()) as { id: string }).id;

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      // The library's managers see whether its files can use a customer-managed key; none is connected here.
      await page.goto(`${hostOrigin(tenant.key)}/documents/libraries/${libraryId}`);
      await expect(page.getByTestId("library-key-status")).toHaveText(m("documents.libraryKey.unavailable"));
      await expectAccessible(page, testInfo, `library-key-${locale}`);
      await page.goto(`${hostOrigin(tenant.key)}/documents/${documentId}`);
      await expect(page.getByText(m("records.document.notRecord"))).toBeVisible();
      await page.getByRole("button", { name: m("records.document.declare") }).click();
      await expect(page.getByLabel(m("records.document.reason"))).toBeVisible();
      await expect(page.getByText(m("records.document.isRecordBody"))).toBeVisible();
      await expectAccessible(page, testInfo, `record-document-${locale}`);

      await page.goto(`${hostOrigin(tenant.key)}/records`);
      await expect(page.getByTestId("record-item").filter({ hasText: "signed-contract.txt" })).toBeVisible();
      await page.getByRole("button", { name: m("records.certificates.issue") }).click();
      await expect(page.getByText(apiText(locale, "validation.records.certificate_empty")).first()).toBeVisible();
      await page.getByLabel(m("records.certificates.code")).fill("AAAA-BBBB-CCCC");
      await page.getByRole("button", { name: m("records.certificates.verify"), exact: true }).click();
      await expect(page.getByText(m("records.certificates.notFound"))).toBeVisible();
      await expectAccessible(page, testInfo, `records-certificates-${locale}`);
    });
  });
}
