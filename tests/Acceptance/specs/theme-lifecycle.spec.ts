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
  });
}
