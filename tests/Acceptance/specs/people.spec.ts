import { request } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createPage, saveAndPublish, tenantApi } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

/**
 * Spec 010 T009: the people directory pages. Two colleagues join, the administrator sets the reporting line by hand
 * (no directory sync), then finds a colleague by name, opens the profile and org chart in chart and list view, sees
 * My team with indirect reports, edits their own profile and changes a field's visibility, in English and Arabic with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`people directory in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const layla = locale === "ar" ? "ليلى ناصر" : "Layla Nasser";
    const omar = locale === "ar" ? "عمر حداد" : "Omar Haddad";

    /** First sign-in provisions the appointed administrator with a display name. */
    async function join(local: string, displayName: string): Promise<string> {
      const context = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": `${tenant.key}.${stack.baseDomain}` } });
      const response = await context.post("/dev/signin", { data: { email: `${local}@${tenant.key}.example`, displayName } });
      expect(response.ok()).toBe(true);
      await context.dispose();
      const api = await tenantApi(tenant);
      const found = await api.get(`/api/v1/people?q=${encodeURIComponent(displayName)}`);
      expect(found.ok()).toBe(true);
      const id = ((await found.json()) as { items: { id: string }[] }).items[0]?.id;
      await api.dispose();
      expect(id).toBeTruthy();
      return id!;
    }

    test.beforeAll(async () => {
      tenant = await createTenant(`ppl-${locale}`, locale, "workplace", ["layla", "omar"]);
      const api = await tenantApi(tenant);
      const own = await api.get("/api/v1/me/profile");
      expect(own.ok()).toBe(true);
      const adminId = ((await own.json()) as { profile: { id: string } }).profile.id;
      await api.dispose();
      const laylaId = await join("layla", layla);
      const omarId = await join("omar", omar);
      const admin = await tenantApi(tenant);
      expect((await admin.put(`/api/v1/people/${laylaId}/manager`, { data: { managerId: adminId } })).ok()).toBe(true);
      expect((await admin.put(`/api/v1/people/${omarId}/manager`, { data: { managerId: laylaId } })).ok()).toBe(true);
      await admin.dispose();
    });

    test("a colleague is found by name and their profile and org chart open", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "people.title"));
      const search = page.getByRole("search");
      await search.getByLabel(msg(locale, "people.search.label")).fill(layla.split(" ")[0]!);
      await search.getByRole("button", { name: msg(locale, "people.search.submit") }).click();
      const results = page.getByTestId("people-results");
      await expect(results.getByRole("link", { name: layla })).toBeVisible();
      await expectAccessible(page, testInfo, `people-search-${locale}`);
      // Results stand alone, and the search is in the address, so a reload shows the same results (spec 010 FR-024, spec 008 FR-065).
      await expect(page.getByRole("heading", { name: msg(locale, "people.newcomers.title") })).toHaveCount(0);
      await expect(page).toHaveURL(/[?&]q=/);
      await page.reload();
      await expect(page.getByTestId("people-results").getByRole("link", { name: layla })).toBeVisible();
      await expect(page.getByRole("search").getByLabel(msg(locale, "people.search.label"))).toHaveValue(layla.split(" ")[0]!);

      await page.getByTestId("people-results").getByRole("link", { name: layla }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(layla);
      const chart = page.getByTestId("org-chart");
      await expect(chart.getByRole("list", { name: msg(locale, "people.org.reports") }).getByRole("link", { name: omar })).toBeVisible();
      await expectAccessible(page, testInfo, `people-profile-${locale}`);

      await page.getByRole("button", { name: msg(locale, "people.org.list") }).click();
      await expect(page.getByTestId("org-list").getByRole("link", { name: omar })).toBeVisible();
      await expect(page.getByRole("button", { name: msg(locale, "people.org.list") })).toHaveAttribute("aria-pressed", "true");
    });

    test("the people component lists new joiners and a chosen person on a page", async ({ page }, testInfo) => {
      const api = await tenantApi(tenant);
      const found = await api.get(`/api/v1/people?q=${encodeURIComponent(omar)}`);
      const omarId = ((await found.json()) as { items: { id: string }[] }).items[0]!.id;
      const created = await createPage(api, "T09", `people-${Date.now().toString(36)}`);
      const main = created.draft.regions.find((r) => r.id === "main")!;
      await saveAndPublish(api, created, [
        ...created.draft.regions.filter((r) => r.id !== "main"),
        {
          ...main,
          placements: [
            { id: "joiners", componentRef: "people.list@1", props: { source: "newcomers", top: 6 } },
            { id: "chosen", componentRef: "people.list@1", props: { source: "named", people: [omarId] } },
          ],
        },
      ]);
      await api.dispose();

      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/p/${created.slug}`);
      const placement = (id: string) => page.locator(`.xp-placement[data-placement-id="${id}"]`);
      await expect(placement("joiners").getByRole("heading", { name: msg(locale, "people.component.newcomers") })).toBeVisible();
      await expect(placement("joiners").getByRole("link", { name: layla })).toBeVisible();
      await expect(placement("chosen").getByRole("link", { name: omar })).toBeVisible();
      await expect(placement("chosen").getByRole("link", { name: layla })).toHaveCount(0);
      await expectAccessible(page, testInfo, `people-component-${locale}`);
    });

    test("My team lists direct reports and, on request, indirect ones", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/team`);
      const team = page.getByTestId("team-list");
      // The card also carries quick actions named after the person (DLS-113), so the profile link is matched exactly.
      await expect(team.getByRole("link", { name: layla, exact: true })).toBeVisible();
      await expect(team.getByRole("link", { name: omar, exact: true })).toHaveCount(0);
      await page.getByLabel(msg(locale, "people.team.indirect")).check();
      await expect(team.getByRole("link", { name: omar, exact: true })).toBeVisible();
      await expectAccessible(page, testInfo, `people-team-${locale}`);
    });

    test("a person edits a field they own on their profile", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/me`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "people.me.title"));
      await page.getByLabel(msg(locale, "people.field.timeZone")).selectOption("Asia/Dubai");
      await page.getByRole("button", { name: msg(locale, "people.me.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "people.me.saved") })).toBeVisible();
      await expectAccessible(page, testInfo, `people-me-${locale}`);
    });

    test("an administrator changes who sees a field", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/admin/people`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(msg(locale, "people.admin.title"));
      await expectAccessible(page, testInfo, `people-admin-${locale}`);
      const field = msg(locale, "people.field.mobilePhone");
      await page.getByLabel(`${field}: ${msg(locale, "people.admin.visibility")}`).selectOption("hr");
      await page.getByRole("button", { name: msg(locale, "people.admin.save") }).click();
      await expect(page.getByRole("status").filter({ hasText: msg(locale, "people.admin.saved") })).toBeVisible();
      await page.reload();
      await expect(page.getByLabel(`${field}: ${msg(locale, "people.admin.visibility")}`)).toHaveValue("hr");
    });
  });
}
