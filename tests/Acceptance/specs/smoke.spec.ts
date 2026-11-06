import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg } from "../support/messages";
import { createTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Smoke suite: the full stack (web, API, PostgreSQL) works end to end for the R0 journeys every later test builds on.
 * Specs: 001 (tenant hosts), 003 (sign-in and sessions), 007 (language, direction, accessibility).
 * The browser locale drives the language of signed-out pages; after sign-in the API resolves it (spec 007 US1).
 */
test.describe("smoke in English", () => {
  test.use({ locale: "en-US" });

  test("a signed-out visitor is sent to sign-in, in English and left to right", async ({ page }, testInfo) => {
    const tenant = await createTenant("en", "en");
    await page.goto(hostOrigin(tenant.key));

    await expect(page).toHaveURL(`${hostOrigin(tenant.key)}/signin`);
    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    await expect(page.getByRole("heading", { level: 1, name: msg("en", "signin.title") })).toBeVisible();
    await expectAccessible(page, testInfo, "signin-en");
  });

  test("the appointed administrator signs in and reaches the home page", async ({ page }, testInfo) => {
    const tenant = await createTenant("en", "en");
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");

    await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg("en", "home.title", { name: tenant.adminEmail }));
    await expect(page.getByText(msg("en", "home.subtitle", { tenant: tenant.key }))).toBeVisible();
    await expect(page.getByRole("link", { name: msg("en", "home.openAdmin") })).toBeVisible();
    await expectAccessible(page, testInfo, "home-en");
  });

  test("a session from one tenant is not accepted on another tenant's host", async ({ page }) => {
    const first = await createTenant("en", "en");
    const second = await createTenant("en", "en");
    await signInWithDevForm(page, first.key, first.adminEmail, "en");
    const session = (await page.context().cookies(hostOrigin(first.key))).find((c) => c.name === "tb_session");
    expect(session, "session cookie after sign-in").toBeDefined();

    // Replay the same session token on the other tenant's host: it must not sign anyone in there (spec 001, 003).
    await page.context().addCookies([
      { name: "tb_session", value: session!.value, url: hostOrigin(second.key), httpOnly: true, secure: true, sameSite: "Lax" },
    ]);
    expect((await page.context().cookies(hostOrigin(second.key))).map((c) => c.name)).toContain("tb_session");
    await page.goto(hostOrigin(second.key));

    await expect(page).toHaveURL(`${hostOrigin(second.key)}/signin`);
  });
});

test.describe("smoke in Arabic", () => {
  test.use({ locale: "ar-AE" });

  test("the sign-in page renders in Arabic and right to left", async ({ page }, testInfo) => {
    const tenant = await createTenant("ar", "ar");
    await page.goto(`${hostOrigin(tenant.key)}/signin`);

    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1, name: msg("ar", "signin.title") })).toBeVisible();
    await expectAccessible(page, testInfo, "signin-ar");
  });

  test("an Arabic tenant's administrator signs in and sees the home page right to left", async ({ page }, testInfo) => {
    const tenant = await createTenant("ar", "ar");
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "ar");

    await expect(page.locator("html")).toHaveAttribute("lang", "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg("ar", "home.title", { name: tenant.adminEmail }));
    await expectAccessible(page, testInfo, "home-ar");
  });
});
