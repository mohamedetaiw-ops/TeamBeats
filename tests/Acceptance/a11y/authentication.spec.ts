import { expect, signInWithDevForm, test } from "../support/fixtures";
import { createTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Accessible authentication and input purpose (spec 038 A11Y-022, WCAG 3.3.8 and 1.3.5): the sign-in field names its
 * purpose for autofill and takes pasted text, and a code field takes a pasted code in one go.
 */
test.describe("accessible authentication", () => {
  test("the sign-in email field sets autocomplete and accepts paste", async ({ page, context }) => {
    const tenant = await createTenant("a11y-auth", "en");
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await page.goto(`${hostOrigin(tenant.key)}/signin`);
    const email = page.locator('input[name="email"]');
    await expect(email).toHaveAttribute("autocomplete", "email");
    await page.evaluate((value) => navigator.clipboard.writeText(value), tenant.adminEmail);
    await email.focus();
    await page.keyboard.press("ControlOrMeta+V");
    await expect(email).toHaveValue(tenant.adminEmail);
  });

  test("a verification code field takes a pasted code and offers one-time-code autofill", async ({ page, context }) => {
    const tenant = await createTenant("a11y-auth-code", "en");
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/ui-gallery/components`);
    await expect(page.locator('[data-testid="components-gallery"][data-hydrated="true"]')).toBeAttached();
    const boxes = page.locator(".ui-otp input");
    await expect(boxes.first()).toHaveAttribute("autocomplete", "one-time-code");
    const count = await boxes.count();
    const code = "123456789".slice(0, count);
    await page.evaluate((value) => navigator.clipboard.writeText(value), code);
    await boxes.first().focus();
    await page.keyboard.press("ControlOrMeta+V");
    await expect(page.locator('input[type="hidden"][name="cg-code"]')).toHaveValue(code);
  });
});
