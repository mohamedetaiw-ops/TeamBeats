import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/** A Tuesday at least two days ahead, so the day is a working day within the default 14 days. */
function nextTuesday(): string {
  const day = new Date(Date.now() + 2 * 86_400_000);
  while (day.getUTCDay() !== 2) day.setUTCDate(day.getUTCDate() + 1);
  return day.toISOString().slice(0, 10);
}

/**
 * Spec 031 slice 1: the administrator adds a site with a floor and a desk and opens its labels; Layla finds the desk
 * free on a Tuesday, books it, sees it under My bookings and cancels it. Rules, the parallel race, recurring series,
 * limits, removal, leavers and the tenant boundary are covered by BookingTests. In English and Arabic, with axe.
 * Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`booking in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const site = locale === "ar" ? "مكتب الرياض" : "Riyadh office";
    const desk = locale === "ar" ? "مكتب 1.01" : "Desk 1.01";

    test.beforeAll(async () => {
      tenant = await createTenant(`bkg-${locale}`, locale, "workplace_plus", ["layla"]);
    });

    test("an administrator adds a site, a floor and a desk", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking/manage`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.manage.pageTitle"));
      const main = page.getByRole("main");
      await main.getByLabel(m("booking.manage.siteName")).fill(site);
      await main.getByLabel(m("booking.manage.timeZone")).fill("Asia/Riyadh");
      await expectAccessible(page, testInfo, `booking-manage-${locale}`);
      await main.getByRole("button", { name: m("booking.manage.createSite") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(site);

      await main.getByLabel(m("booking.manage.floorName")).fill(locale === "ar" ? "الطابق الأول" : "First floor");
      await main.getByRole("button", { name: m("booking.manage.addFloor") }).click();
      await expect(page.getByText(m("booking.manage.floorAdded"))).toBeVisible();
      await main.getByLabel(m("booking.manage.kind")).selectOption("desk");
      await main.getByLabel(m("booking.manage.resourceName")).fill(desk);
      await main.getByRole("button", { name: m("booking.manage.addResource") }).click();
      await expect(page.getByText(m("booking.manage.resourceAdded", { name: desk }))).toBeVisible();
      await expectAccessible(page, testInfo, `booking-site-${locale}`);

      await main.getByRole("link", { name: m("booking.manage.labels") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.manage.labels"));
      await expect(page.getByRole("list", { name: m("booking.manage.labels") }).getByRole("listitem")).toHaveCount(1);
      await expectAccessible(page, testInfo, `booking-labels-${locale}`);
    });

    test("an employee books a desk, finds it in My bookings and cancels it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.pageTitle"));
      const main = page.getByRole("main");
      await main.getByLabel(m("booking.find.kind")).selectOption("desk");
      await main.getByLabel(m("booking.find.date")).fill(nextTuesday());
      await main.getByRole("button", { name: m("booking.find.search") }).click();
      const results = main.getByRole("list", { name: m("booking.find.resultsLabel") });
      await expect(results.getByRole("listitem")).toHaveCount(1);
      await expectAccessible(page, testInfo, `booking-find-${locale}`);
      await results.getByRole("button", { name: m("booking.find.book", { name: desk }) }).click();
      await expect(page.getByText(m("booking.find.bookedTitle"))).toBeVisible();

      await page.getByRole("link", { name: m("booking.mine.pageTitle") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.mine.pageTitle"));
      const list = main.getByRole("list", { name: m("booking.mine.listLabel") });
      await expect(list.getByRole("listitem")).toHaveCount(1);
      await expect(list.getByText(m("booking.status.confirmed"))).toBeVisible();
      await expectAccessible(page, testInfo, `booking-mine-${locale}`);
      await list.getByRole("button", { name: m("booking.mine.cancel") }).click();
      await list.getByRole("button", { name: m("booking.mine.cancelConfirm") }).click();
      await expect(list.getByText(m("booking.status.cancelled"))).toBeVisible();
    });
  });
}
