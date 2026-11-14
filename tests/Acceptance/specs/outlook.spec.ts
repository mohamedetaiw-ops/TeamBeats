import type { Page, Route } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 018 T016: My mail and My calendar on a published page, in English and Arabic with axe, and the Outlook section
 * of personal settings. From R2 My calendar renders the spec 037 calendar card (spec 018 FR-017, spec 032 FR-070), so
 * its rows and states are the card's, covered by calendar.spec.ts; here it only has to be that card. The browser's calls to the employee's mail, calendar and Outlook endpoints are answered here
 * with the documented response shapes, one state per placement (selected by its top or days setting), because the
 * Microsoft sign-in redirect needs HTTPS on the tenant host. The server side of every state, with the fake Microsoft
 * Graph, is covered by tests/Integration/MailCalendarTests.cs.
 */
const unique = (prefix: string) => `${prefix}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

const dubaiDate = (offsetDays: number) =>
  new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Dubai", year: "numeric", month: "2-digit", day: "2-digit" }).format(Date.now() + offsetDays * 86_400_000);
/**
 * The calendar groups events by day in Asia/Dubai, so events a few minutes from now fall on tomorrow late in the
 * Dubai evening. The browser clock is fixed at noon in Dubai on today's Dubai date and the fixtures count from it.
 */
const now = Date.parse(`${dubaiDate(0)}T08:00:00Z`);
const minutes = (n: number) => new Date(now + n * 60_000).toISOString();

function message(id: string, extra: Record<string, unknown> = {}) {
  return {
    id, sender: `Sender ${id}`, senderAddress: `${id}@contoso.example`, subject: `Subject ${id}`, receivedAt: minutes(-5),
    isRead: true, importance: "normal", hasAttachments: false, preview: null, webLink: `https://outlook.office.com/mail/fake/${id}`, ...extra,
  };
}

function mail(top: number, view: string) {
  const empty = { asOf: null, unreadCount: null, messages: [], composeLink: null };
  switch (top) {
    case 1: return { state: "no_mailbox", ...empty };
    case 2: return { state: "not_connected", ...empty };
    case 3: return { state: "degraded", asOf: minutes(-20), unreadCount: 1, messages: [message("old", { isRead: false })], composeLink: null };
    case 4: return { state: "conditional_access", ...empty };
    default:
      return {
        state: "ok", asOf: minutes(0), unreadCount: 7, composeLink: "https://outlook.office.com/mail/deeplink/compose",
        messages: view === "all"
          ? [message("other"), message("first", { isRead: false, importance: "high" })]
          : [
            message("first", { isRead: false, importance: "high" }),
            message("second", { hasAttachments: true }),
            // Spec 036 DLS-091: a row in the other language starts at the same edge as its neighbours.
            message("arabic", { sender: "فاطمة الزهراء", subject: "إقفال الشهر" }),
          ],
      };
  }
}

function item(id: string, extra: Record<string, unknown>) {
  return {
    id, source: "outlook", subject: `Event ${id}`, start: minutes(60), end: minutes(90), isAllDay: false, isPrivate: false, free: false,
    tentative: false, location: null, onlineJoinUrl: null, webLink: `https://outlook.office.com/calendar/fake/${id}`, ...extra,
  };
}

/** days=2 fails until the employee retries, which asks with refresh=true (development renders run effects twice). */
function calendar(days: number, refresh: boolean) {
  const frame = { asOf: minutes(0), timeZone: "Asia/Dubai", from: minutes(-60), to: minutes(days * 1440) };
  if (days === 1) return { state: "admin_setup", ...frame, items: [] };
  if (days === 2 && !refresh) return null;
  if (days === 2) return { state: "ok", ...frame, items: [] };
  return {
    state: "ok", ...frame,
    items: [
      item("holiday", { subject: "Company holiday", isAllDay: true, start: `${dubaiDate(0)}T00:00:00Z`, end: `${dubaiDate(1)}T00:00:00Z`, free: true }),
      item("standup", { subject: "Standup", start: minutes(5), end: minutes(20), onlineJoinUrl: "https://teams.microsoft.com/l/meetup-join/fake" }),
      item("maybe", { subject: "Maybe lunch", start: minutes(30), end: minutes(45), tentative: true, location: "Cafe" }),
      item("private", { subject: null, isPrivate: true, start: minutes(50), end: minutes(55) }),
      item("later", { subject: "Review", start: `${dubaiDate(2)}T08:00:00Z`, end: `${dubaiDate(2)}T09:00:00Z`, onlineJoinUrl: "https://teams.microsoft.com/l/meetup-join/later" }),
    ],
  };
}

async function answer(route: Route, body: unknown) {
  if (body === null) {
    await route.fulfill({ status: 503, contentType: "application/problem+json", body: JSON.stringify({ code: "unavailable" }) });
  } else {
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  }
}

/** Answers the employee's own Outlook endpoints; returns the requests seen so a test can check what the browser asked. */
async function fakeOutlook(page: Page) {
  const seen: string[] = [];
  let connected = true;
  await page.route("**/api/v1/me/mail?*", (route) => {
    const url = new URL(route.request().url());
    seen.push(url.pathname + url.search);
    return answer(route, mail(Number(url.searchParams.get("top")), url.searchParams.get("view") ?? "focused"));
  });
  await page.route("**/api/v1/me/calendar?*", (route) => {
    const url = new URL(route.request().url());
    seen.push(url.pathname + url.search);
    return answer(route, calendar(Number(url.searchParams.get("days")), url.searchParams.get("refresh") === "true"));
  });
  await page.route("**/api/v1/me/outlook", (route) => {
    seen.push(`${route.request().method()} /api/v1/me/outlook`);
    if (route.request().method() === "DELETE") {
      connected = false;
      return route.fulfill({ status: 204 });
    }
    return answer(route, { state: connected ? "ok" : "not_connected", features: connected ? ["mail", "calendar"] : [], connectedAt: null, expiresAt: null });
  });
  return seen;
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`My mail and My calendar in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US", timezoneId: "Asia/Dubai" });
    let tenant: TestTenant;
    let slug: string;

    test.beforeAll(async () => {
      tenant = await createTenant(`outlook-${locale}`, locale, "workplace_plus");
      const api = await tenantApi(tenant);
      const page = await createPage(api, "T01", unique("outlook"));
      await saveAndPublish(api, page, page.draft.regions.map((r, i) => ({
        ...r,
        placements: i > 0 ? [] : [
          { id: "mail", componentRef: "outlook.my_mail@1", props: { view: "focused", top: 5 } },
          { id: "calendar", componentRef: "outlook.my_calendar@1", props: { days: 7, hidePrivate: true } },
          { id: "no-mailbox", componentRef: "outlook.my_mail@1", props: { top: 1 } },
          { id: "not-connected", componentRef: "outlook.my_mail@1", props: { top: 2 } },
          { id: "degraded", componentRef: "outlook.my_mail@1", props: { top: 3 } },
          { id: "managed", componentRef: "outlook.my_mail@1", props: { top: 4 } },
        ],
      })));
      slug = page.slug;
      await api.dispose();
    });

    test("mail and calendar show their data, markers, links and every state", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const seen = await fakeOutlook(page);
      await page.clock.setFixedTime(now);
      await page.goto(`${hostOrigin(tenant.key)}/p/${slug}`);

      const mails = page.getByRole("region", { name: msg(locale, "outlook.mail.title") });
      const calendars = page.getByRole("region", { name: msg(locale, "calendar.title") });
      await expect(mails).toHaveCount(5);
      await expect(calendars).toHaveCount(1);

      // My mail: unread count, newest Focused messages with text markers, each opening Outlook in a new tab.
      const main = mails.first();
      await expect(main.getByRole("listitem")).toHaveCount(3);
      // Every subject, Arabic or English, starts at the row's start edge in the page's direction (DLS-091).
      const starts = await main.locator(".outlook-subject").evaluateAll((nodes, rtl) => nodes.map((n) => {
        const range = document.createRange();
        range.selectNodeContents(n);
        const text = range.getBoundingClientRect();
        const box = n.getBoundingClientRect();
        return Math.round(rtl ? box.right - text.right : text.left - box.left);
      }), locale === "ar");
      expect(starts).toEqual([0, 0, 0]);
      await expect(main).toContainText(await page.evaluate((l) => new Intl.NumberFormat(l).format(7), locale));
      const first = main.getByRole("listitem").first();
      await expect(first).toContainText(msg(locale, "outlook.mail.markers.unread"));
      await expect(first).toContainText(msg(locale, "outlook.mail.markers.important"));
      await expect(main.getByRole("listitem").nth(1)).toContainText(msg(locale, "outlook.mail.markers.attachment"));
      const link = first.getByRole("link");
      await expect(link).toHaveAttribute("href", "https://outlook.office.com/mail/fake/first");
      await expect(link).toHaveAttribute("target", "_blank");
      await expect(link).toContainText(msg(locale, "outlook.mail.opensOutlook"));
      await expect(main.getByRole("link", { name: new RegExp(msg(locale, "outlook.mail.compose")) })).toHaveAttribute("href", "https://outlook.office.com/mail/deeplink/compose");

      // The Focused and All switch asks for the other view.
      await main.getByRole("radio", { name: msg(locale, "outlook.mail.views.all") }).check();
      await expect(main.getByRole("radio", { name: msg(locale, "outlook.mail.views.all") })).toBeChecked();
      await expect(main.getByRole("listitem").first()).toContainText("Subject other");
      expect(seen).toContain("/api/v1/me/mail?view=all&top=5");

      // States: no mailbox, not connected with Connect Outlook back to this page, degraded with its age, managed device.
      await expect(mails.nth(1).getByRole("status")).toContainText(msg(locale, "outlook.state.noMailbox"));
      const connect = mails.nth(2).getByRole("link", { name: msg(locale, "outlook.state.connect") });
      await expect(connect).toHaveAttribute("href", `/api/v1/me/outlook/connect?returnTo=${encodeURIComponent(`/p/${slug}`)}`);
      await expect(mails.nth(3).getByRole("listitem")).toHaveCount(1);
      await expect(mails.nth(3).getByRole("status")).toContainText(msg(locale, "outlook.state.stale", { time: "§" }).split("§")[0].trim());
      await mails.nth(3).getByRole("button", { name: msg(locale, "outlook.state.retry") }).click();
      await expect.poll(() => seen.filter((s) => s === "/api/v1/me/mail?view=focused&top=3&refresh=true").length).toBe(1);
      await expect(mails.nth(4).getByRole("status")).toContainText(msg(locale, "outlook.state.conditionalAccess"));
      await expect(mails.nth(4).getByRole("link", { name: msg(locale, "outlook.state.openOutlook") })).toHaveAttribute("target", "_blank");

      // My calendar is the calendar card: the week strip and agenda rows, not the R1 list, and no call to the R1 endpoint.
      const cal = calendars.first();
      await expect(cal.getByRole("list", { name: msg(locale, "calendar.weekLabel") }).getByRole("listitem")).toHaveCount(7);
      await expect(cal.locator(".outlook-day")).toHaveCount(0);
      expect(seen.some((s) => s.startsWith("/api/v1/me/calendar"))).toBe(false);

      await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      await expectAccessible(page, testInfo, `outlook-${locale}`);
    });

    test("the Outlook section in Connected apps disconnects and offers Connect Outlook", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const seen = await fakeOutlook(page);
      await page.goto(`${hostOrigin(tenant.key)}/settings`);
      const section = page.getByRole("region", { name: msg(locale, "outlook.settings.title") });
      await expect(section.getByRole("status")).toContainText(msg(locale, "outlook.settings.connected"));
      await section.getByRole("button", { name: msg(locale, "outlook.settings.disconnect") }).click();
      await expect(section.getByRole("status")).toContainText(msg(locale, "outlook.settings.notConnected"));
      expect(seen).toContain("DELETE /api/v1/me/outlook");
      await expect(section.getByRole("link", { name: msg(locale, "outlook.settings.connect") })).toHaveAttribute(
        "href", `/api/v1/me/outlook/connect?returnTo=${encodeURIComponent("/settings")}`);
      await expectAccessible(page, testInfo, `outlook-settings-${locale}`);
    });
  });
}
