import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { seedHome } from "../support/home";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 008 FR-056 and FR-058: the footer every page shows and the extra home views as tabs after My day, both as the
 * staging seed configures them (src/Api.DevHost/seeds/staging-home.json), then changed on the shell settings page.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`home views and footer in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`homeviews-${locale}`, locale, "workplace_plus");
      await seedHome(tenant, { extras: true });
    });

    test("the footer shows the tenant's organization, links and classification on every page", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      const footer = page.getByRole("contentinfo");
      await expect(footer.getByText(locale === "ar" ? "TeamBeats التجريبية" : "TeamBeats Demo")).toBeVisible();
      const links = footer.getByRole("navigation", { name: msg(locale, "shell.footer.label") });
      await expect(links.getByRole("link", { name: locale === "ar" ? "الخصوصية" : "Privacy" })).toHaveAttribute("href", "/policies?q=LEG-001");
      await expect(links.getByRole("link", { name: locale === "ar" ? "الاستخدام المقبول" : "Acceptable use" })).toHaveAttribute("href", "/policies?q=SEC-001");
      await expect(links.getByRole("link", { name: locale === "ar" ? "إمكانية الوصول" : "Accessibility" })).toHaveAttribute("href", "/settings");
      await expect(footer.getByText(msg(locale, "shell.footer.classification", { value: locale === "ar" ? "داخلي" : "Internal" }))).toBeVisible();
      await expectAccessible(page, testInfo, `home-footer-${locale}`);

      // Another page has the same footer.
      await page.goto(`${hostOrigin(tenant.key)}/services`);
      await expect(page.getByRole("contentinfo").getByText(locale === "ar" ? "TeamBeats التجريبية" : "TeamBeats Demo")).toBeVisible();
    });

    test("My day comes first and each extra view opens as a tab", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/`);
      const tabs = page.getByRole("navigation", { name: msg(locale, "home.views.label") });
      const names = locale === "ar" ? ["يومي", "قائد الفريق", "أرض المصنع"] : ["My day", "Team lead", "Plant floor"];
      await expect(tabs.getByRole("link")).toHaveText(names);
      await expect(tabs.getByRole("link", { name: names[0] })).toHaveAttribute("aria-current", "page");
      await expect(page.getByTestId("home-briefing")).toBeVisible();

      await tabs.getByRole("link", { name: names[2] }).click();
      await expect(page).toHaveURL(/\?view=plant-floor$/);
      await expect(tabs.getByRole("link", { name: names[2] })).toHaveAttribute("aria-current", "page");
      await expect(page.getByRole("heading", { name: locale === "ar" ? "أسبوع السلامة: خمسة أيام وخمس عادات" : "Safety Week: five days, five habits" })).toBeVisible();
      await expect(page.getByTestId("home-briefing")).toHaveCount(0);
      await expectAccessible(page, testInfo, `home-view-floor-${locale}`);
      await page.screenshot({ path: testInfo.outputPath(`home-view-floor-${locale}.png`), fullPage: true });

      // A view this viewer does not get falls back to My day.
      await page.goto(`${hostOrigin(tenant.key)}/?view=nothing-here`);
      await expect(tabs.getByRole("link", { name: names[0] })).toHaveAttribute("aria-current", "page");
    });
  });
}

test.describe("shell settings", () => {
  test.use({ locale: "en-US" });
  let tenant: TestTenant;

  test.beforeAll(async () => {
    tenant = await createTenant("shellsettings", "en", "workplace_plus");
    await seedHome(tenant, { extras: true });
  });

  test("an administrator renames the organization and removes a view, and the shell follows", async ({ page }, testInfo) => {
    await signInWithDevForm(page, tenant.key, tenant.adminEmail, "en");
    await page.goto(`${hostOrigin(tenant.key)}/admin/shell`);
    await expect(page.getByRole("heading", { level: 1, name: msg("en", "shell_settings.title") })).toBeVisible();
    await page.getByLabel(msg("en", "shell_settings.footer.organizationEn")).fill("Northwind Works");
    await page.getByRole("button", { name: msg("en", "shell_settings.views.remove", { number: "1" }) }).click();
    await expectAccessible(page, testInfo, "shell-settings");
    await page.getByRole("button", { name: msg("en", "shell_settings.save") }).click();
    await expect(page.getByText(msg("en", "shell_settings.saved"))).toBeVisible();

    // A link the API refuses is explained, and nothing changes.
    await page.getByLabel(msg("en", "shell_settings.footer.url", { number: "1" })).fill("javascript:alert(1)");
    await page.getByRole("button", { name: msg("en", "shell_settings.save") }).click();
    await expect(page.getByText(msg("en", "shell_settings.failed"))).toBeVisible();

    await page.goto(`${hostOrigin(tenant.key)}/`);
    await expect(page.getByRole("contentinfo").getByText("Northwind Works")).toBeVisible();
    await expect(page.getByRole("contentinfo").getByRole("link", { name: "Privacy" })).toHaveAttribute("href", "/policies?q=LEG-001");
    const tabs = page.getByRole("navigation", { name: msg("en", "home.views.label") });
    await expect(tabs.getByRole("link")).toHaveText(["My day", "Plant floor"]);
  });
});

/**
 * The shell settings layout (spec 036 DLS-049, DLS-050, DLS-067): at desktop and phone widths, in English and Arabic,
 * every control and hint stays inside its link or view card, no two controls overlap, labels in one grid row line up,
 * and the page never scrolls sideways.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`shell settings layout in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`shelllayout-${locale}`, locale, "workplace_plus");
      await seedHome(tenant, { extras: true });
    });

    test("fields stay inside their cards at desktop and phone widths", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      for (const [name, size] of [
        ["desktop", { width: 1440, height: 900 }],
        ["phone", { width: 390, height: 844 }],
      ] as const) {
        await page.setViewportSize(size);
        await page.goto(`${hostOrigin(tenant.key)}/admin/shell`);
        await expect(page.getByRole("heading", { level: 1, name: msg(locale, "shell_settings.title") })).toBeVisible();
        await expect(page.getByRole("group", { name: msg(locale, "shell_settings.footer.linkTitle", { number: "1" }) })).toBeVisible();
        const problems = await page.evaluate(() => {
          const found: string[] = [];
          const inside = (a: DOMRect, b: DOMRect) => a.left >= b.left - 0.5 && a.right <= b.right + 0.5;
          const overlap = (a: DOMRect, b: DOMRect) => a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5;
          if (document.documentElement.scrollWidth > window.innerWidth + 1) found.push("page scrolls sideways");
          document.querySelectorAll(".shell-settings-section").forEach((section, s) => {
            const card = section.getBoundingClientRect();
            section.querySelectorAll(".field").forEach((field, f) => {
              const box = field.getBoundingClientRect();
              if (!inside(box, card)) found.push(`section ${s} field ${f} leaves the card`);
              field.querySelectorAll("input, select, .field-hint").forEach((part) => {
                if (!inside(part.getBoundingClientRect(), box)) found.push(`section ${s} field ${f} ${part.tagName} leaves its field`);
              });
            });
            const controls = [...section.querySelectorAll("input, select")].map((c) => c.getBoundingClientRect());
            controls.forEach((a, i) => controls.slice(i + 1).forEach((b, j) => overlap(a, b) && found.push(`section ${s} controls ${i} and ${i + j + 1} overlap`)));
          });
          document.querySelectorAll(".shell-settings-grid").forEach((grid, g) => {
            const tops = new Map<number, number[]>();
            [...grid.children].forEach((field) => {
              const row = Math.round((field as HTMLElement).offsetTop);
              const label = field.querySelector("label")?.getBoundingClientRect().top ?? 0;
              tops.set(row, [...(tops.get(row) ?? []), label]);
            });
            tops.forEach((labels) => {
              if (Math.max(...labels) - Math.min(...labels) > 1) found.push(`grid ${g} labels do not line up`);
            });
          });
          return found;
        });
        expect(problems, `${name} ${locale}`).toEqual([]);
        await page.screenshot({ path: testInfo.outputPath(`shell-settings-${locale}-${name}.png`), fullPage: true });
      }
      await expectAccessible(page, testInfo, `shell-settings-${locale}`);
    });
  });
}
