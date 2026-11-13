import { test } from "../support/fixtures";
import { createTenant, type TestTenant } from "../support/platform";
import { signInWithDevForm } from "../support/fixtures";
import { seedHome } from "../support/home";
import { hostOrigin } from "../support/stack";

const out = process.env.SHOTS ?? "/tmp/shots";
const routes = (process.env.ROUTES ?? "/,/services,/policies,/admin/users").split(",");
const widths = (process.env.WIDTHS ?? "390").split(",").map(Number);

for (const locale of ["en", "ar"] as const) {
  test.describe(`shots ${locale}`, () => {
    test.use({ locale });
    let tenant: TestTenant;
    test.beforeAll(async () => {
      tenant = await createTenant("shots", locale, "workplace_plus");
      await seedHome(tenant, { extras: true });
    });
    test(`pages ${locale}`, async ({ page }) => {
      test.setTimeout(300_000);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      for (const width of widths) {
        await page.setViewportSize({ width, height: 844 });
        for (const route of routes) {
          await page.goto(`${hostOrigin(tenant.key)}${route}`);
          await page.waitForLoadState("networkidle");
          const name = route === "/" ? "home" : route.replace(/^\//, "").replaceAll("/", "-");
          await page.screenshot({ path: `${out}/${name}-${locale}-${width}.png`, fullPage: false });
        }
      }
    });
  });
}
