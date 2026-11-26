import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 010 R2 T015 (US8 scenarios 1 to 3): Layla sees how complete her profile is and puts the prompt away; a colleague
 * opens Omar's profile and sees his assistant and, while his delegation is active, who his approvals go to. The rules
 * are covered by ProfileRelationshipTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`profile relations in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let omar: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    async function idOf(email: string): Promise<string> {
      const client = await tenantApiAs(tenant, email);
      const id = ((await (await client.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
      await client.dispose();
      return id;
    }

    test.beforeAll(async () => {
      tenant = await createTenant(`rel-${locale}`, locale, "workplace_plus", ["layla", "omar", "sara"]);
      omar = await idOf(`omar@${tenant.key}.example`);
      const sara = await idOf(`sara@${tenant.key}.example`);
      const admin = await tenantApi(tenant);
      const set = await admin.put(`/api/v1/people/${omar}/assistants`, { data: { assistantIds: [sara] } });
      expect(set.ok(), await set.text()).toBe(true);
      await admin.dispose();
      const client = await tenantApiAs(tenant, `omar@${tenant.key}.example`);
      const today = new Date().toISOString().slice(0, 10);
      const end = new Date(Date.now() + 5 * 86_400_000).toISOString().slice(0, 10);
      const delegated = await client.put("/api/v1/me/delegation", { data: { delegateId: sara, startsOn: today, endsOn: end } });
      expect(delegated.ok(), await delegated.text()).toBe(true);
      await client.dispose();
    });

    test("Layla sees what her profile misses and puts the prompt away", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/me`);
      const prompt = page.getByTestId("profile-completeness");
      await expect(prompt).toBeVisible();
      await expect(prompt.getByRole("link", { name: m("people.completeness.photo") })).toBeVisible();
      await expectAccessible(page, testInfo, `people-completeness-${locale}`);
      await prompt.getByRole("button", { name: m("people.completeness.dismiss") }).click();
      await expect(prompt).toHaveCount(0);
      await page.reload();
      await expect(page.getByTestId("profile-completeness")).toHaveCount(0);
    });

    test("a colleague sees Omar's assistant and where his approvals go", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/${omar}`);
      await expect(page.getByTestId("person-assistants")).toContainText("sara");
      await expect(page.getByTestId("person-delegation")).toContainText("sara");
      await expectAccessible(page, testInfo, `people-relations-${locale}`);
    });
  });
}
