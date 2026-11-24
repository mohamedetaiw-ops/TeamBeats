import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 033 R2 T014: publishing a brand. The administrator keeps a draft that employees do not see, publishes it with a
 * reason after its impact report (T015), finds every version with how it went live, and rolls back to an earlier one. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`theme lifecycle in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`thl-${locale}`, locale, "enterprise_gov");
    });

    test("keep a draft, publish it with a reason and roll back", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding`);
      const lifecycle = page.getByTestId("theme-lifecycle");
      await expect(lifecycle).toBeVisible();
      const radius = page.getByRole("textbox", {
        name: msg(locale, "brand.tokenInput", { token: msg(locale, "brand.tokenNames.radius.md"), mode: msg(locale, "brand.modes.light") }),
        exact: true,
      });
      await page.locator("details.brand-group").filter({ has: page.locator('tr[data-token="radius.md"]') }).locator("summary").click();
      // The value is typed again until the editor has hydrated and keeps it.
      await expect(async () => {
        await radius.fill("4px");
        await expect(radius).toHaveValue("4px", { timeout: 1_000 });
      }).toPass();
      await page.getByRole("button", { name: msg(locale, "brand.saveDraft") }).click();
      await expect(page.getByTestId("brand-status")).toHaveText(msg(locale, "brand.draftSaved"));
      await expect(lifecycle.getByTestId("theme-draft-state")).not.toHaveText(msg(locale, "brand.lifecycle.noDraft", { version: "1" }));
      await expect(lifecycle.getByRole("button", { name: msg(locale, "brand.lifecycle.publish") })).toBeVisible();
      // Spec 036 DLS-023: the draft shows the design review checklist before it is published or sent for approval.
      const review = lifecycle.getByTestId("theme-review");
      await expect(review.getByRole("heading", { name: msg(locale, "brand.lifecycle.review.title") })).toBeVisible();
      await expect(review.getByRole("listitem")).toHaveCount(7);
      await expect(review).toContainText(msg(locale, "brand.lifecycle.review.items.contrast"));
      await expectAccessible(page, testInfo, `theme-lifecycle-${locale}`);

      await lifecycle.getByLabel(msg(locale, "brand.lifecycle.reason")).fill("Softer corners");
      await lifecycle.getByRole("button", { name: msg(locale, "brand.lifecycle.publish") }).click();
      // The impact report comes first (T015): what the draft reaches and a preview of a scope using it.
      const dialog = page.getByRole("dialog");
      await expect(dialog.getByTestId("theme-impact")).toBeVisible();
      await expect(dialog.locator("figure.brand-preview")).toHaveCount(2);
      await expectAccessible(page, testInfo, `theme-impact-${locale}`);
      await dialog.getByRole("button", { name: msg(locale, "builder.impact.publish") }).click();
      await expect(lifecycle.getByTestId("theme-result")).toHaveText(msg(locale, "brand.lifecycle.published"));
      const releases = lifecycle.getByTestId("theme-releases").getByRole("listitem");
      await expect(releases).toHaveCount(2);
      await expect(releases.first()).toContainText("Softer corners");
      await expect(releases.first()).toContainText(msg(locale, "brand.lifecycle.inForce"));

      await lifecycle.getByRole("button", { name: msg(locale, "brand.lifecycle.revertLabel", { version: "1" }) }).click();
      await lifecycle.getByRole("button", { name: msg(locale, "brand.lifecycle.revertConfirm"), exact: true }).click();
      await expect(lifecycle.getByTestId("theme-result")).toHaveText(msg(locale, "brand.lifecycle.reverted", { version: "1" }));
      await expect(releases).toHaveCount(3);
      await expect(releases.first()).toContainText(msg(locale, "brand.lifecycle.how.reverted"));
      await expect(radius).toHaveValue("");
    });

    test("preview the brand as an organization on each mode, language and device", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/branding`);
      await page.getByRole("link", { name: msg(locale, "brand.previewPage.open") }).click();
      await page.waitForURL(/\/admin\/branding\/preview/);
      const frame = page.getByTestId("theme-preview-frame");
      await expect(frame).toBeVisible();
      await expect(frame).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      const other = locale === "ar" ? "en" : "ar";
      // Choices are made again until the controls have hydrated and keep them.
      await expect(async () => {
        await page.getByRole("radiogroup", { name: msg(locale, "brand.previewPage.language") }).getByRole("radio", { name: msg(locale, `brand.previewPage.languages.${other}`), exact: true }).check();
        await expect(frame).toHaveAttribute("dir", other === "ar" ? "rtl" : "ltr", { timeout: 1_000 });
      }).toPass();
      await page.getByRole("radiogroup", { name: msg(locale, "brand.previewPage.mode") }).getByRole("radio", { name: msg(locale, "brand.modes.dark"), exact: true }).check();
      await expect(frame).toHaveAttribute("data-mode", "dark");
      await page.getByRole("radiogroup", { name: msg(locale, "brand.previewPage.device") }).getByRole("radio", { name: msg(locale, "brand.previewPage.devices.phone"), exact: true }).check();
      await expect(page.locator(".brand-stage")).toHaveAttribute("data-device", "phone");
      await page.getByRole("radiogroup", { name: msg(locale, "brand.previewPage.source") }).getByRole("radio", { name: msg(locale, "brand.previewPage.sources.current"), exact: true }).check();
      await expect(page.getByTestId("theme-preview-caption")).toHaveText(
        msg(locale, "brand.previewPage.captionCurrent", { scope: msg(locale, "brand.previewPage.wholeOrganization") }),
      );
      await expectAccessible(page, testInfo, `theme-preview-${locale}`);
    });
  });
}
