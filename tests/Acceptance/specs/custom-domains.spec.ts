import { request } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 033 R2 T021 (US7, FR-010): an administrator adds a custom domain, publishes the TXT record it shows (to the fake
 * DNS here), verifies it and sets the domain's own title and sign-in message. The domain then serves the workspace's
 * sign-in page with that wording. In English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`custom domains in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const title = { en: "Acme Hub", ar: "مركز أكمي" };
    const message = { en: "Use your Acme account to sign in.", ar: "استخدم حساب أكمي لتسجيل الدخول." };

    test.beforeAll(async () => {
      tenant = await createTenant(`cdm-${locale}`, locale, "workplace_plus");
    });

    test("an administrator verifies a domain and it serves the sign-in page with its own wording", async ({ page }, testInfo) => {
      // Any name under localhost reaches this machine, and it is outside the installation's own domain.
      const host = `${tenant.key}-site.localhost`;
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/tenant`);
      await page.getByRole("link", { name: msg(locale, "admin.tenant.domains.open"), exact: true }).click();
      await expect(page).toHaveURL(/\/admin\/tenant\/domains$/);
      await expect(page.getByText(msg(locale, "admin.tenant.domains.emptyTitle"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "domains-empty");

      const add = page.getByRole("button", { name: msg(locale, "admin.tenant.domains.add"), exact: true });
      // The host is typed again until the form has hydrated and keeps it.
      await expect(async () => {
        await page.getByLabel(msg(locale, "admin.tenant.domains.host"), { exact: true }).fill(host);
        await expect(add).toBeEnabled({ timeout: 1_000 });
      }).toPass();
      await add.click();
      const panel = page.getByTestId("custom-domain");
      await expect(panel).toContainText(msg(locale, "admin.tenant.domains.states.pending"));
      const recordName = await panel.getByLabel(msg(locale, "admin.tenant.domains.recordName"), { exact: true }).inputValue();
      const recordValue = await panel.getByLabel(msg(locale, "admin.tenant.domains.recordValue"), { exact: true }).inputValue();

      // Verifying before the record is published says it was not found and keeps the domain waiting.
      await panel.getByRole("button", { name: msg(locale, "admin.tenant.domains.verify"), exact: true }).click();
      await expect(page.getByText(msg(locale, "admin.tenant.domains.failures.record_missing")).first()).toBeVisible();

      const dns = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": `${tenant.key}.${stack.baseDomain}` } });
      const published = await dns.put(`/dev/fake/dns/txt?name=${encodeURIComponent(recordName)}`, { data: { values: [recordValue] } });
      expect(published.ok(), await published.text()).toBe(true);
      await dns.dispose();

      await panel.getByRole("button", { name: msg(locale, "admin.tenant.domains.verify"), exact: true }).click();
      await expect(page.getByText(msg(locale, "admin.tenant.domains.verifiedDone", { host }), { exact: true })).toBeVisible();
      await expect(panel).toContainText(msg(locale, "admin.tenant.domains.states.verified"));

      const language = msg(locale, `admin.org.starter.languages.${locale}`);
      await panel.getByLabel(msg(locale, "admin.tenant.domains.appTitle", { language }), { exact: true }).fill(title[locale]);
      await panel.getByLabel(msg(locale, "admin.tenant.domains.signInMessage", { language }), { exact: true }).fill(message[locale]);
      await panel.getByRole("button", { name: msg(locale, "admin.tenant.domains.save"), exact: true }).click();
      await expect(page.getByText(msg(locale, "admin.tenant.domains.saved"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, "domains-verified");

      // The verified domain serves the workspace, with the wording set for it.
      const port = stack.webPort ? `:${stack.webPort}` : "";
      const visitorContext = await page.context().browser()!.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const visitor = await visitorContext.newPage();
      await visitor.goto(`${stack.scheme}://${host}${port}/signin`);
      await expect(visitor.getByRole("heading", { level: 1, name: msg(locale, "signin.titleAt", { name: title[locale] }), exact: true })).toBeVisible();
      await expect(visitor.getByTestId("signin-message")).toHaveText(message[locale]);
      await expect(visitor).toHaveTitle(new RegExp(title[locale]));
      await expectAccessible(visitor, testInfo, "domain-signin");
      await visitorContext.close();
    });
  });
}
