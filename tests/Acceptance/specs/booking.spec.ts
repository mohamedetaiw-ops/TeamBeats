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

/** The next quarter hour in Riyadh (UTC+3, no daylight saving) as a date and a time, so check-in is open for a booking there. */
function nextRiyadhSlot(): { date: string; start: string; end: string } {
  const local = new Date(Date.now() + 3 * 3_600_000);
  local.setUTCMinutes(Math.floor(local.getUTCMinutes() / 15) * 15 + 15, 0, 0);
  const end = new Date(local.getTime() + 3_600_000);
  const hhmm = (d: Date) => d.toISOString().slice(11, 16);
  return { date: local.toISOString().slice(0, 10), start: hhmm(local), end: hhmm(end) };
}

/**
 * Spec 031 slices 1 and 2: the administrator adds a site with a floor and a desk and opens its labels; Layla finds the desk
 * free on a Tuesday, books it, sees it under My bookings and cancels it; picks a free time from the desk's day, then she scans a camera kit's code, books it
 * for now and checks in from the scan page; the administrator then reads the site's usage report. Approval holds, expiry, release, the no-show rule, rules, the parallel race, recurring series,
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
    const camera = locale === "ar" ? "طقم الكاميرا" : "Camera kit";
    let code = "";

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
      await main.getByLabel(m("booking.manage.kind")).selectOption("equipment");
      await main.getByLabel(m("booking.manage.resourceName")).fill(camera);
      await main.getByRole("button", { name: m("booking.manage.addResource") }).click();
      await expect(page.getByText(m("booking.manage.resourceAdded", { name: camera }))).toBeVisible();
      await expectAccessible(page, testInfo, `booking-site-${locale}`);

      await main.getByRole("link", { name: m("booking.manage.labels") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.manage.labels"));
      const labels = page.getByRole("list", { name: m("booking.manage.labels") }).getByRole("listitem");
      await expect(labels).toHaveCount(2);
      const cameraLabel = labels.filter({ hasText: camera });
      await expect(cameraLabel.getByRole("img", { name: m("booking.manage.qrAlt", { name: camera }) })).toBeVisible();
      code = (await cameraLabel.locator("code").textContent())?.trim() ?? "";
      expect(code).toMatch(/^[0-9a-f]{32}$/);
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

    test("an employee picks a free time from the day of a desk", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking`);
      const main = page.getByRole("main");
      await main.getByLabel(m("booking.find.kind")).selectOption("desk");
      await main.getByLabel(m("booking.find.date")).fill(nextTuesday());
      await main.getByRole("button", { name: m("booking.find.search") }).click();
      const results = main.getByRole("list", { name: m("booking.find.resultsLabel") });
      await results.getByRole("button", { name: m("booking.find.grid.show") }).click();
      const grid = main.getByRole("group", { name: m("booking.find.grid.label", { name: desk }) });
      const slot = grid.getByRole("button", { name: m("booking.find.grid.free", { start: "11:00", end: "11:30" }) });
      await slot.focus();
      await page.keyboard.press("Enter");
      await expect(main.getByLabel(m("booking.find.start"), { exact: true })).toHaveValue("11:00");
      await expect(main.getByLabel(m("booking.find.end"), { exact: true })).toHaveValue("11:30");
      await expectAccessible(page, testInfo, `booking-grid-${locale}`);
    });

    test("an employee scans a code, books the equipment for now and checks in", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking/scan/${code}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.checkIn.pageTitle"));
      const main = page.getByRole("main");
      await expect(main.getByText(m("booking.checkIn.noneTitle"))).toBeVisible();
      await expectAccessible(page, testInfo, `booking-scan-none-${locale}`);

      const slot = nextRiyadhSlot();
      await main.getByRole("link", { name: m("booking.checkIn.find") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.pageTitle"));
      await main.getByLabel(m("booking.find.kind")).selectOption("equipment");
      await main.getByLabel(m("booking.find.date")).fill(slot.date);
      await main.getByLabel(m("booking.find.start")).fill(slot.start);
      await main.getByLabel(m("booking.find.end")).fill(slot.end);
      await main.getByRole("button", { name: m("booking.find.search") }).click();
      const results = main.getByRole("list", { name: m("booking.find.resultsLabel") });
      await results.getByRole("button", { name: m("booking.find.book", { name: camera }) }).click();
      await expect(page.getByText(m("booking.find.bookedTitle"))).toBeVisible();

      await page.goto(`${hostOrigin(tenant.key)}/booking/scan/${code}`);
      await main.getByRole("button", { name: m("booking.checkIn.action") }).click();
      await expect(main.getByText(m("booking.checkIn.doneTitle"))).toBeVisible();
      await expect(main.getByText(m("booking.status.checked_in"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `booking-scan-done-${locale}`);
    });

    test("an administrator sees how the site was used without names", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking`);
      await page.getByRole("main").getByRole("link", { name: m("booking.reports.pageTitle") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.reports.pageTitle"));
      const main = page.getByRole("main");
      await expect(main.getByRole("heading", { name: m("booking.reports.total", { site }) })).toBeVisible();
      const table = main.getByRole("table");
      await expect(table.getByRole("rowheader", { name: camera })).toBeVisible();
      await expect(table.getByRole("rowheader", { name: desk })).toBeVisible();
      await expect(table).not.toContainText("layla");
      await main.getByLabel(m("booking.reports.groupBy")).selectOption("week");
      await expect(main.getByRole("heading", { level: 2, name: m("booking.reports.group.week") })).toBeVisible();
      await expectAccessible(page, testInfo, `booking-reports-${locale}`);
    });

    test("an administrator closes the site for a day and opens it again", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking/manage`);
      await page.getByRole("main").getByRole("link", { name: site }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(site);
      const closures = page.getByRole("region", { name: m("booking.closures.title") });
      const reason = locale === "ar" ? "تسرب مياه في الطابق الأول" : "Water leak on the first floor";
      await closures.getByLabel(m("booking.closures.date")).fill(nextTuesday());
      await closures.getByLabel(m("booking.closures.reason")).fill(reason);
      await closures.getByRole("button", { name: m("booking.closures.close") }).click();
      const list = closures.getByRole("list", { name: m("booking.closures.listLabel") });
      await expect(list.getByText(reason)).toBeVisible();
      await expectAccessible(page, testInfo, `booking-closures-${locale}`);
      await list.getByRole("button", { name: m("booking.closures.reopen") }).click();
      await list.getByRole("button", { name: m("booking.closures.reopenConfirm") }).click();
      await expect(closures.getByText(m("booking.closures.none"))).toBeVisible();
    });
  });
}
