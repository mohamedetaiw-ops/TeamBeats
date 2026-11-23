import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 032 R2 T029 (FR-007): an editor resizes a region of the builder by keyboard within the layout's limits; the
 * neighbour in the row takes the difference and the draft keeps the widths. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`region resize in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let pageId: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`prz-${locale}`, locale);
      const admin = await tenantApi(tenant);
      pageId = (await createPage(admin, "T03", `side-${locale}`)).id;
      await admin.dispose();
    });

    test("make the side region narrower", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/pages/${pageId}`);
      const width = page.getByTestId("builder-width-aside");
      await expect(width).toBeVisible();
      await expect(width).toHaveAttribute("aria-valuetext", msg(locale, "builder.editor.columns", { count: "4" }));
      await width.focus();
      // Home takes the narrowest width whichever way the page reads.
      await page.keyboard.press("Home");
      await expect(width).toHaveAttribute("aria-valuetext", msg(locale, "builder.editor.columns", { count: "3" }));
      await expectAccessible(page, testInfo, `page-resize-${locale}`);

      const admin = await tenantApi(tenant);
      await expect
        .poll(async () => (await (await admin.get(`/api/v1/experience/pages/${pageId}`)).json()).draft.spans, { timeout: 15_000 })
        .toEqual({ aside: 3, main: 9 });
      await admin.dispose();
    });
  });
}
