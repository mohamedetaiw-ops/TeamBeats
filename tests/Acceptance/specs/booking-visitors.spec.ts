import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/** A site time zone where it is now between 06:00 and 18:00, so a visit later today fits in the day. */
function daytimeZone(): string {
  for (const zone of ["Asia/Riyadh", "Asia/Tokyo", "Europe/London", "America/New_York", "Pacific/Honolulu"]) {
    const hour = Number(new Intl.DateTimeFormat("en-GB", { timeZone: zone, hour: "2-digit", hourCycle: "h23" }).format(new Date()));
    if (hour >= 6 && hour < 18) return zone;
  }
  throw new Error("No daytime zone.");
}

/** Today in the zone, and a start half an hour or more from now on a quarter hour, with an end an hour later. */
function soon(zone: string): { date: string; start: string; end: string } {
  const date = new Intl.DateTimeFormat("en-CA", { timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  const [h, m] = new Intl.DateTimeFormat("en-GB", { timeZone: zone, hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(new Date()).split(":").map(Number);
  const minutes = Math.floor((h! * 60 + m!) / 15) * 15 + 30;
  const at = (x: number) => `${String(Math.floor(x / 60)).padStart(2, "0")}:${String(x % 60).padStart(2, "0")}`;
  return { date, start: at(minutes), end: at(minutes + 60) };
}

/**
 * Spec 031 R3 US8: Layla expects a visitor later today and sees the code; reception (the administrator) sees the day's
 * visitors with the host, checks the visitor in by that code and out again. Limits, emails, the code working only on the
 * day, changes, leavers, the 30 day purge and the boundaries are covered by BookingVisitTests. In English and Arabic,
 * with axe. Names and addresses are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`booking visitors in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let code = "";
    const zone = daytimeZone();
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const visitor = locale === "ar" ? "نادية حداد" : "Nadia Haddad";
    const site = locale === "ar" ? "مكتب الدوحة" : "Doha office";

    test.beforeAll(async () => {
      tenant = await createTenant(`bkgvi-${locale}`, locale, "workplace_plus", ["layla"]);
      const api = await tenantApiAs(tenant, tenant.adminEmail);
      const response = await api.post("/api/v1/booking/manage/sites", { data: { names: { [locale]: site }, timeZone: zone, opensAt: "00:00:00", closesAt: "23:59:00" } });
      expect(response.ok(), await response.text()).toBeTruthy();
      await api.dispose();
    });

    test("Layla expects a visitor later today and sees the code", async ({ page }, testInfo) => {
      const when = soon(zone);
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking/bookings`);
      await page.getByRole("main").getByRole("link", { name: m("booking.visits.pageTitle") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.visits.pageTitle"));
      const main = page.getByRole("main");
      await expect(main.getByText(m("booking.visits.emptyTitle"))).toBeVisible();
      await main.getByLabel(m("booking.visits.date"), { exact: true }).fill(when.date);
      await main.getByLabel(m("booking.visits.start"), { exact: true }).fill(when.start);
      await main.getByLabel(m("booking.visits.end"), { exact: true }).fill(when.end);
      await main.getByLabel(m("booking.visits.name"), { exact: true }).fill(visitor);
      await main.getByLabel(m("booking.visits.email"), { exact: true }).fill(`nadia.${locale}@visitor.example`);
      await main.getByLabel(m("booking.visits.company"), { exact: true }).fill("Example Trading");
      await expectAccessible(page, testInfo, `booking-visits-form-${locale}`);
      await main.getByRole("button", { name: m("booking.visits.register") }).click();
      await expect(main.getByText(m("booking.visits.registered", { name: visitor }))).toBeVisible();
      const card = main.getByRole("listitem").filter({ hasText: visitor });
      await expect(card.getByText(m("booking.visits.status.expected"), { exact: true })).toBeVisible();
      code = (await card.locator(".booking-code").textContent())!.trim();
      expect(code).toMatch(/^[A-Z2-9]{8}$/);
      await expectAccessible(page, testInfo, `booking-visits-${locale}`);
    });

    test("reception checks the visitor in by code and out again", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking`);
      await page.getByRole("main").getByRole("link", { name: m("booking.reception.pageTitle") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.reception.pageTitle"));
      const main = page.getByRole("main");
      const card = main.getByRole("listitem").filter({ hasText: visitor });
      await expect(card.getByText(m("booking.visits.status.expected"), { exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `booking-reception-${locale}`);
      await main.getByLabel(m("booking.reception.code")).fill(code.toLowerCase());
      await main.getByRole("button", { name: m("booking.reception.checkIn"), exact: true }).click();
      await expect(main.getByText(m("booking.reception.arrived", { name: visitor }))).toBeVisible();
      await expect(card.getByText(m("booking.visits.status.arrived"), { exact: true })).toBeVisible();
      await card.getByRole("button", { name: m("booking.reception.checkOutName", { name: visitor }) }).click();
      await expect(card.getByText(m("booking.visits.status.left"), { exact: true })).toBeVisible();
    });
  });
}
