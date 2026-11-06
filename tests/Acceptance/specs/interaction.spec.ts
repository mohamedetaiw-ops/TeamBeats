import type { Page } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 008 T003 (User Story 5 and FR-006, FR-007): shared states, one primary action per context, a destructive look,
 * linked and announced errors that keep values, confirm and undo, message priorities, both densities at 200% zoom and
 * reduced motion. The gallery exists only with the development fixtures.
 */
test.skip(stack.target === "remote", "The interface gallery ships only with the development fixtures.");

async function openGallery(page: Page, tenant: TestTenant, locale: LaunchLocale, query = "") {
  await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
  await page.goto(`${hostOrigin(tenant.key)}/ui-gallery${query}`);
  await expect(page.getByRole("heading", { level: 1, name: msg(locale, "gallery.title") })).toBeVisible();
  // Interactive once hydrated.
  await expect(page.locator('.gallery[data-hydrated="true"]')).toBeAttached();
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`interaction standards in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`ui-${locale}`, locale);
    });

    test("states, action hierarchy, form errors and selection", async ({ page }, testInfo) => {
      await openGallery(page, tenant, locale);
      for (const kind of ["loading", "success", "empty", "error", "denied", "offline", "unsupported"]) {
        await expect(page.locator(`.ui-state[data-state="${kind}"]`)).toContainText(msg(locale, `ui.state.${kind}.title`));
      }

      // At most one primary action in each task context, and destructive actions look different from it.
      for (const context of await page.locator("[data-context]").all()) {
        expect(await context.locator('[data-variant="primary"]').count()).toBeLessThanOrEqual(1);
      }
      const primary = page.locator('[data-context="form"] [data-variant="primary"]');
      const destructive = page.locator('[data-variant="destructive"]').first();
      const look = (l: typeof primary) => l.evaluate((el) => `${getComputedStyle(el).backgroundColor}|${getComputedStyle(el).color}`);
      expect(await look(destructive)).not.toBe(await look(primary));

      // Errors are linked to the field and announced, and the typed value is kept.
      const field = page.getByLabel(msg(locale, "gallery.form.name"));
      await field.fill("ab");
      await primary.click();
      const error = page.getByRole("alert").filter({ hasText: msg(locale, "gallery.form.error") });
      await expect(error).toBeVisible();
      await expect(field).toHaveValue("ab");
      await expect(field).toHaveAttribute("aria-invalid", "true");
      const errorId = await error.getAttribute("id");
      expect((await field.getAttribute("aria-describedby"))?.split(" ")).toContain(errorId);
      await field.fill("Onboarding");
      await primary.click();
      await expect(page.locator('[data-context="form"] [role="status"]')).toHaveText(msg(locale, "gallery.form.saved"));

      // Bulk selection states the count and the consequence, and can be cancelled.
      await page.getByLabel(msg(locale, "gallery.selection.items.one")).check();
      await page.getByLabel(msg(locale, "gallery.selection.items.two")).check();
      const bar = page.locator(".ui-selection");
      await expect(bar).toContainText(msg(locale, "gallery.selection.consequence"));
      await expect(bar).toContainText(locale === "en" ? "2 items selected" : "عنصران محددان");
      await bar.getByRole("button", { name: msg(locale, "gallery.selection.cancel") }).click();
      await expect(bar).toHaveCount(0);

      await expectAccessible(page, testInfo, `ui-gallery-${locale}`);
    });

    test("destructive actions need confirmation and offer undo; critical messages persist and low ones do not block", async ({ page }) => {
      await page.clock.install();
      await openGallery(page, tenant, locale);
      const draft = page.getByTestId("draft-state");

      // One press only asks; focus lands on the safe choice and Escape cancels.
      await page.getByRole("button", { name: msg(locale, "gallery.confirm.delete") }).click();
      await expect(draft).toHaveText(msg(locale, "gallery.confirm.present"));
      await expect(page.getByRole("button", { name: msg(locale, "gallery.confirm.no") })).toBeFocused();
      await page.keyboard.press("Escape");
      await expect(page.getByRole("alertdialog")).toHaveCount(0);
      await expect(draft).toHaveText(msg(locale, "gallery.confirm.present"));

      await page.getByRole("button", { name: msg(locale, "gallery.confirm.delete") }).click();
      await page.getByRole("button", { name: msg(locale, "gallery.confirm.yes"), exact: true }).click();
      await expect(draft).toHaveText(msg(locale, "gallery.confirm.deleted"));
      await page.getByRole("button", { name: msg(locale, "gallery.confirm.undo") }).click();
      await expect(draft).toHaveText(msg(locale, "gallery.confirm.present"));

      await page.getByRole("button", { name: msg(locale, "gallery.messages.showCritical") }).click();
      await page.getByRole("button", { name: msg(locale, "gallery.messages.showLow") }).click();
      const critical = page.locator('.ui-message[data-priority="critical"]');
      const low = page.locator('.ui-message[data-priority="low"]');
      await expect(critical).toBeVisible();
      await expect(page.locator(".ui-messages-critical")).toHaveAttribute("role", "alert");
      await expect(low).toBeVisible();
      // The page stays usable while messages show.
      await page.getByLabel(msg(locale, "gallery.form.name")).fill("Still typing");
      await page.mouse.move(0, 0);
      await page.clock.runFor(60_000);
      await expect(low).toHaveCount(0);
      await expect(critical).toBeVisible();
      await critical.getByRole("button", { name: msg(locale, "ui.messages.dismiss") }).click();
      await expect(critical).toHaveCount(0);
    });

    test("both densities keep every action at 200% zoom, and reduced motion stops the shimmer", async ({ page }) => {
      for (const density of ["comfortable", "compact"]) {
        await openGallery(page, tenant, locale, `?density=${density}`);
        await expect(page.locator("main")).toHaveAttribute("data-density", density);
        // 200% zoom of a 1280 px window is a 640 px layout viewport.
        await page.setViewportSize({ width: 640, height: 800 });
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        expect(overflow, `horizontal overflow in ${density}`).toBeLessThanOrEqual(0);
        for (const button of await page.locator("main button").all()) {
          await expect(button).toBeVisible();
          const box = (await button.boundingBox())!;
          expect(Math.min(box.width, box.height), "target size").toBeGreaterThanOrEqual(24);
        }
      }
      const spacing = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--tb-space-md").trim());
      expect(spacing).toBe("12px");

      const duration = () => page.locator(".ui-skeleton").first().evaluate((el) => parseFloat(getComputedStyle(el).animationDuration));
      expect(await duration()).toBeGreaterThan(0.5);
      await page.emulateMedia({ reducedMotion: "reduce" });
      expect(await duration()).toBeLessThan(0.01);
    });
  });
}
