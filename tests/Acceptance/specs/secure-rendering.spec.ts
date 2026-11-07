import { expect, signInWithDevForm, test } from "../support/fixtures";
import { createTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/** Spec 026 FR-025 (T015): every page is served with a content security policy that runs scripts only with its nonce. */
test.describe("secure rendering", () => {
  test.use({ locale: "en-US", cspViolationsExpected: true });

  test("pages carry a nonce policy without inline script, and the nonce changes per request", async ({ page }) => {
    const tenant = await createTenant("en", "en");
    const first = await page.goto(`${hostOrigin(tenant.key)}/signin`);
    const policy = first?.headers()["content-security-policy"] ?? "";
    const script = policy.split(";").map((d) => d.trim()).find((d) => d.startsWith("script-src")) ?? "";
    expect(script).toMatch(/'nonce-[A-Za-z0-9+/=]+'/);
    expect(script).not.toContain("unsafe-inline");
    expect(policy).toContain("object-src 'none'");

    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    const home = await page.goto(`${hostOrigin(tenant.key)}/`);
    const again = home?.headers()["content-security-policy"] ?? "";
    expect(again).not.toEqual(policy);

    // Injected markup with an inline event handler, the usual cross-site scripting path, does not run.
    const ran = await page.evaluate(
      () =>
        new Promise<boolean>((resolve) => {
          const holder = document.createElement("div");
          holder.innerHTML = '<img alt="" src="/missing.png" onerror="window.__tbInjected = true">';
          const img = holder.querySelector("img")!;
          img.addEventListener("error", () => setTimeout(() => resolve((window as unknown as { __tbInjected?: boolean }).__tbInjected === true), 50));
          document.body.appendChild(holder);
        }),
    );
    expect(ran).toBe(false);
  });
});
