import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 011 R2 FR-010 in the browser: a contributor is offered to connect Microsoft 365, then "Edit in browser", gets the
 * Office for the web link and checks the edit in. The browser's calls to the document's Office endpoints are answered
 * here with the documented shapes, because the Microsoft sign-in redirect needs HTTPS on the tenant host; the server
 * side with the fake Microsoft Graph and OneDrive is covered by tests/Integration/OfficeEditingTests.cs.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`office editing in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string) => msg(locale, key);

    test.beforeAll(async () => {
      tenant = await createTenant(`off-${locale}`, locale, "workplace_plus");
    });

    test("a contributor edits a Word document in the browser and checks it in", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const library = await api.post("/api/v1/documents/libraries", { data: { names: { [locale]: locale === "ar" ? "السياسات" : "Policies" }, scope: "tenant" } });
      expect(library.status()).toBe(201);
      const libraryId = ((await library.json()) as { id: string }).id;
      const file = await api.post(`/api/v1/documents/libraries/${libraryId}/files?name=plan.docx&publish=true`, {
        headers: { "content-type": "application/octet-stream" },
        data: Buffer.from("Plan"),
      });
      expect(file.status()).toBe(201);
      const doc = (await file.json()) as { id: string };

      let state = "not_connected";
      const editUrl = "https://fake-onedrive.example/edit/01FAKE";
      await page.route(`**/api/v1/documents/${doc.id}/office`, (route) =>
        route.fulfill({ contentType: "application/json", body: JSON.stringify({ state, editUrl: state === "editing" ? editUrl : null, startedAt: null }) }),
      );
      await page.route(`**/api/v1/documents/${doc.id}/office/start`, (route) => {
        state = "editing";
        return route.fulfill({ contentType: "application/json", body: JSON.stringify({ state, editUrl, startedAt: new Date().toISOString() }) });
      });
      await page.route(`**/api/v1/documents/${doc.id}/office/checkin`, async (route) => {
        expect(route.request().postDataJSON()).toEqual({ note: "Updated in Word", publish: false });
        state = "ready";
        const current = await api.get(`/api/v1/documents/${doc.id}`);
        return route.fulfill({ contentType: "application/json", body: await current.text() });
      });

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/documents/${doc.id}`);
      const connect = page.getByRole("link", { name: m("documents.document.officeConnect") });
      await expect(connect).toHaveAttribute("href", /\/api\/v1\/me\/outlook\/connect\?returnTo=%2Fdocuments%2F/);

      state = "ready";
      await page.reload();
      await page.getByRole("button", { name: m("documents.document.officeEdit") }).click();
      await expect(page.getByRole("heading", { level: 2, name: m("documents.document.officeTitle") })).toBeVisible();
      await expect(page.getByRole("main").getByRole("status")).toHaveText(m("documents.document.officeStarted"));
      await expect(page.getByTestId("office-open")).toHaveAttribute("href", editUrl);
      await expect(page.getByTestId("office-open")).toHaveAttribute("target", "_blank");
      await expectAccessible(page, testInfo, `office-editing-${locale}`);

      const form = page.locator("form", { has: page.getByRole("heading", { name: m("documents.document.officeTitle") }) });
      await form.getByLabel(m("documents.document.note")).fill("Updated in Word");
      await form.getByRole("button", { name: m("documents.document.officeCheckIn") }).click();
      await expect(page.getByRole("main").getByRole("status")).toHaveText(m("documents.document.officeCheckedIn"));
      await expect(page.getByRole("heading", { name: m("documents.document.officeTitle") })).toHaveCount(0);
      await expect(page.getByRole("button", { name: m("documents.document.officeEdit") })).toBeVisible();
    });
  });
}
