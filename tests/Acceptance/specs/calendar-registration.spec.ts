import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

const day = 86_400_000;
const iso = (ms: number) => new Date(ms).toISOString().slice(0, 10);
/** Today in Asia/Dubai, the tenant zone. */
const dubaiToday = () => iso(Date.now() + 4 * 3_600_000);
/** A small file that starts with the PDF signature, enough for the type check. */
const pdf = Buffer.from("%PDF-1.4\n% fictional agenda\n%%EOF\n");

async function signedIn(tenant: TestTenant, email: string): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

/**
 * Spec 037 R3 in the browser: registering for an event with seats and a waiting list, the calendar file and the
 * event's attachments in the agenda; on the manage page, an event with registration and a file attached. In English
 * and Arabic, with axe on every screen. Data is fictional and set up through the API.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`calendar registration in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US", timezoneId: "Asia/Dubai" });
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    let tenant: TestTenant;
    const today = dubaiToday();
    const tomorrow = iso(Date.parse(today) + day);
    const workshop = locale === "ar" ? "ورشة الكتابة" : "Writing workshop";

    test.beforeAll(async () => {
      tenant = await createTenant(`calr-${locale}`, locale, "workplace", ["layla", "omar"]);
      const admin = await signedIn(tenant, tenant.adminEmail);
      const event = await admin.post("/api/v1/calendar/admin-events", {
        data: {
          titles: { en: "Writing workshop", ar: "ورشة الكتابة" }, kind: "company", allDay: false,
          start: `${tomorrow}T13:00:00+04:00`, end: `${tomorrow}T14:00:00+04:00`, timeZone: "Asia/Dubai", location: "Room 2",
          rsvp: false, registration: true, capacity: 1, waitlist: true, reminder: "1h",
        },
      });
      expect(event.status(), await event.text()).toBe(201);
      const { id } = (await event.json()) as { id: string };
      const file = await admin.post(`/api/v1/calendar/admin-events/${id}/attachments?name=agenda.pdf`, { headers: { "Content-Type": "application/pdf" }, data: pdf });
      expect(file.status(), await file.text()).toBe(201);
      // Omar takes the only seat, so Layla is offered the waiting list.
      const omar = await signedIn(tenant, `omar@${tenant.key}.example`);
      const occurrences = (await (await omar.get(`/api/v1/calendar/events?from=${today}&to=${iso(Date.parse(today) + 3 * day)}&layers=company`)).json()) as {
        items: { sourceId: string; occurrenceKey: string }[];
      };
      const key = occurrences.items.find((i) => i.sourceId === id)!.occurrenceKey;
      expect((await omar.post(`/api/v1/calendar/events/${id}/registration`, { data: { occurrenceKey: key } })).ok()).toBe(true);
      await Promise.all([admin.dispose(), omar.dispose()]);
    });

    test("a full event offers the waiting list, the calendar file and the agenda file", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/calendar?view=agenda&date=${today}`);
      const row = page.locator(".ui-cal-row").filter({ hasText: workshop });
      await expect(row).toHaveCount(1);
      await row.getByRole("button", { expanded: false }).click();
      const registration = row.getByTestId("calendar-registration");
      await expect(registration.getByText(m("calendar.registration.full"))).toBeVisible();
      await expect(row.getByRole("link", { name: "agenda.pdf" })).toBeVisible();
      await expect(row.getByRole("link", { name: m("calendar.actions.calendarFile") })).toHaveAttribute("href", /calendar-file\?occurrence=/);
      await expectAccessible(page, testInfo, `calendar-registration-${locale}`);

      await registration.getByRole("button", { name: m("calendar.registration.joinListFor", { title: workshop }) }).click();
      await expect(registration.getByText(m("calendar.registration.waiting", { position: "1" }))).toBeVisible();
      await registration.getByRole("button", { name: m("calendar.registration.cancelFor", { title: workshop }) }).click();
      await expect(registration.getByText(m("calendar.registration.full"))).toBeVisible();
    });

    test("the manage page creates an event with seats and attaches a file to it", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/calendar/manage`);
      await page.getByRole("button", { name: m("calendar.manage.create") }).click();
      let dialog = page.getByRole("dialog");
      await dialog.getByLabel(m("calendar.manage.titleEn")).fill("Chess club");
      await dialog.getByLabel(m("calendar.manage.timeZone")).fill("Asia/Dubai");
      await dialog.getByLabel(m("calendar.manage.answers")).selectOption("registration");
      await dialog.getByLabel(m("calendar.manage.capacity"), { exact: true }).fill("8");
      await expectAccessible(page, testInfo, `calendar-manage-registration-${locale}`);
      await dialog.getByRole("button", { name: m("calendar.manage.save") }).click();
      await expect(dialog).toBeHidden();

      const item = page.locator(".cal-manage-list li").filter({ hasText: "Chess club" });
      await expect(item).toHaveCount(1);
      await item.getByRole("button", { name: m("calendar.manage.edit") }).click();
      dialog = page.getByRole("dialog");
      await expect(dialog.getByLabel(m("calendar.manage.capacity"), { exact: true })).toHaveValue("8");
      await dialog.getByLabel(m("calendar.attachments.add")).setInputFiles({ name: "rules.pdf", mimeType: "application/pdf", buffer: pdf });
      await expect(dialog.getByText("rules.pdf", { exact: true })).toBeVisible();
      // The upload is done once its progress line is gone and the file stays listed.
      await expect(dialog.locator(".ui-progress-label")).toHaveCount(0);
      await expect(dialog.getByText("rules.pdf", { exact: true })).toBeVisible();
      await expect(dialog.getByRole("alert")).toHaveCount(0);
    });
  });
}
