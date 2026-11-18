import { expect, signInWithDevForm, test } from "../support/fixtures";
import { expectConformant } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi } from "../support/pages";
import { createTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * The accessibility statement (spec 038 US6, A11Y-023): public in both languages, the tenant's own accessibility link
 * as the contact when it configured one, and the footer falling back to the statement when it did not.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`accessibility statement in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    test("anyone can read it signed out, and it says to contact the administrator", async ({ page }, testInfo) => {
      const tenant = await createTenant("a11y-statement", locale);
      await page.goto(`${hostOrigin(tenant.key)}/accessibility`);
      await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expect(page.getByRole("heading", { level: 1, name: msg(locale, "accessibility_statement.title") })).toBeVisible();
      await expect(page.getByRole("heading", { level: 2, name: msg(locale, "accessibility_statement.limitations.title") })).toBeVisible();
      await expect(page.getByText(msg(locale, "accessibility_statement.report.admin"))).toBeVisible();
      await expectConformant(page, testInfo, `statement-signed-out-${locale}`);
    });

    test("signed in, the footer links the statement when the tenant links no accessibility page, and the statement shows the tenant's own page when it does", async ({ page }, testInfo) => {
      const tenant = await createTenant("a11y-statement-in", locale);
      const api = await tenantApi(tenant);
      const footer = (links: unknown[]) =>
        api.put("/api/v1/experience/shell/definition", {
          data: {
            start: ["identity", "global_navigation"], center: ["search"], end: ["settings"], below: ["breadcrumbs"], utilities: [],
            footer: { organization: { en: "Example Holding", ar: "الشركة المثال" }, links, classification: null },
          },
        });
      try {
        const privacy = { kind: "privacy", labels: { en: "Privacy", ar: "الخصوصية" }, url: "/policies" };
        expect((await footer([privacy])).ok()).toBe(true);
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await page.goto(`${hostOrigin(tenant.key)}/`);
        const nav = page.getByRole("contentinfo").getByRole("navigation", { name: msg(locale, "shell.footer.label") });
        await nav.getByRole("link", { name: msg(locale, "shell.footer.accessibility") }).click();
        await expect(page).toHaveURL(/\/accessibility$/);
        await expect(page.getByRole("heading", { level: 1, name: msg(locale, "accessibility_statement.title") })).toBeVisible();
        await expect(page.getByRole("link", { name: msg(locale, "accessibility_statement.settings.open") })).toHaveAttribute("href", "/settings");
        await expectConformant(page, testInfo, `statement-signed-in-${locale}`);

        const own = { kind: "accessibility", labels: { en: "Our accessibility office", ar: "مكتب إمكانية الوصول لدينا" }, url: "/policies?q=ACC" };
        const saved = await footer([privacy, own]);
        expect(saved.ok(), await saved.text()).toBe(true);
        await page.reload();
        const main = page.getByRole("main");
        await expect(main.getByRole("link", { name: own.labels[locale] })).toHaveAttribute("href", "/policies?q=ACC");
        await expect(main.getByText(msg(locale, "accessibility_statement.report.admin"))).toHaveCount(0);
      } finally {
        await api.dispose();
      }
    });
  });
}
