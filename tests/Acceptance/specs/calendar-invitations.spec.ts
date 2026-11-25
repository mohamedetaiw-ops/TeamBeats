import type { Route } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 018 R3 T019 (FR-015): accept, tentative or decline an Outlook invitation from the calendar workspace, in English
 * and Arabic with axe. The Microsoft sign-in needs HTTPS on the tenant host, so the browser's calls for the meeting layer
 * and the answer are answered here with the documented shapes; the server side, with the fake Microsoft Graph, is in
 * tests/Integration/MailCalendarInvitationTests.cs.
 */
const dubaiToday = () => new Date(Date.now() + 4 * 3_600_000).toISOString().slice(0, 10);

function meeting(id: string, title: string, hour: number, extra: Record<string, unknown>) {
  const day = dubaiToday();
  return {
    id: `outlook:${id}`, layer: "meeting", source: "outlook", sourceId: id, title, allDay: false,
    start: `${day}T${String(hour).padStart(2, "0")}:00:00+04:00`, end: `${day}T${String(hour + 1).padStart(2, "0")}:00:00+04:00`,
    startDate: null, endDate: null, timeZone: "Asia/Dubai", status: "confirmed", visibility: "personal", actions: ["open_source"],
    location: null, onlineUrl: null, webLink: `https://outlook.office.com/calendar/fake/${id}`, description: null, free: false, isPrivate: false,
    halfStart: false, halfEnd: false, count: null, rsvp: null, occurrenceKey: null, recurring: false, leaveRequestId: null, invitation: null, ...extra,
  };
}

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`invitation answers in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US", timezoneId: "Asia/Dubai" });
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    let tenant: TestTenant;

    test.beforeAll(async () => {
      tenant = await createTenant(`inv-${locale}`, locale, "workplace_plus", []);
    });

    test("an invitation is answered from its details and a refused answer says why", async ({ page }, testInfo) => {
      const answers: { id: string; body: unknown }[] = [];
      // What Outlook holds, so a reload after an answer shows it as the server would.
      const held: Record<string, string> = { budget: "not_responded", old: "tentative" };
      const after: Record<string, string> = { accept: "accepted", tentative: "tentative" };
      await page.route("**/api/v1/calendar/events?**", (route: Route) =>
        route.fulfill({
          json: {
            timeZone: "Asia/Dubai", from: dubaiToday(), to: dubaiToday(), layers: ["meeting"], nextCursor: null,
            sources: [{ source: "outlook", state: "ok", asOf: new Date().toISOString() }],
            items: [
              meeting("budget", "Budget review", 10, { actions: ["answer", "open_source"], invitation: held.budget }),
              meeting("mine", "Team planning", 14, {}),
              meeting("old", "Old review", 16, { actions: ["answer", "open_source"], invitation: held.old }),
            ],
          },
        }),
      );
      await page.route("**/api/v1/me/calendar/events/*/response", async (route: Route) => {
        const id = decodeURIComponent(route.request().url().split("/events/")[1]!.split("/")[0]!);
        const body = route.request().postDataJSON() as { response: string };
        answers.push({ id, body });
        if (id !== "old") held[id] = after[body.response] ?? held[id]!;
        await route.fulfill({ json: { state: id === "old" ? "not_found" : "ok" } });
      });
      const open = async (row: ReturnType<typeof page.locator>) => {
        const closed = row.getByRole("button", { expanded: false });
        if (await closed.count()) await closed.click();
      };

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/calendar?view=agenda&date=${dubaiToday()}`);
      const budget = page.locator(".ui-cal-row").filter({ hasText: "Budget review" });
      await budget.getByRole("button", { expanded: false }).click();
      const control = budget.getByTestId("calendar-invitation");
      await expect(control.getByRole("group", { name: m("calendar.invitation.question") })).toBeVisible();
      await control.getByRole("checkbox", { name: m("calendar.invitation.notify") }).uncheck();
      await control.getByRole("button", { name: m("calendar.invitation.accept"), exact: true }).click();
      await expect.poll(() => answers.length).toBe(1);
      await open(budget);
      await expect(control.getByRole("button", { name: m("calendar.invitation.accept"), exact: true })).toHaveAttribute("aria-pressed", "true");
      expect(answers[0]).toEqual({ id: "budget", body: { response: "accept", notifyOrganizer: false } });
      await expectAccessible(page, testInfo, `calendar-invitation-${locale}`);

      // The employee's own meeting offers no answer.
      const mine = page.locator(".ui-cal-row").filter({ hasText: "Team planning" });
      await mine.getByRole("button", { expanded: false }).click();
      await expect(mine.getByTestId("calendar-invitation")).toHaveCount(0);

      // A meeting gone from Outlook says so and keeps the earlier answer.
      const old = page.locator(".ui-cal-row").filter({ hasText: "Old review" });
      await old.getByRole("button", { expanded: false }).click();
      const oldControl = old.getByTestId("calendar-invitation");
      await expect(oldControl.getByRole("button", { name: m("calendar.invitation.tentative"), exact: true })).toHaveAttribute("aria-pressed", "true");
      await oldControl.getByRole("button", { name: m("calendar.invitation.decline"), exact: true }).click();
      await expect(oldControl.getByRole("alert")).toHaveText(m("calendar.invitation.states.not_found"));
      await expect(oldControl.getByRole("button", { name: m("calendar.invitation.tentative"), exact: true })).toHaveAttribute("aria-pressed", "true");
    });
  });
}
