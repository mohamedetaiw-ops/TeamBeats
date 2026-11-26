import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 010 R3 T019 (US9): Layla turns on skill suggestions on her own profile, asks for them, sees "Legal translation"
 * with the community post it came from, and adds it to her skills. The rules (consent, own items only, dismissals,
 * limits) are covered by SkillSuggestionTests. In English and Arabic, with axe. Names and content are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`skill suggestions in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    const skill = locale === "ar" ? "الترجمة القانونية" : "Legal translation";
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);

    test.beforeAll(async () => {
      tenant = await createTenant(`sgs-${locale}`, locale, "workplace_plus", ["layla"]);
      const admin = await tenantApi(tenant);
      const current = await admin.get("/api/v1/people/skills");
      const saved = await admin.put("/api/v1/people/skills", {
        headers: { "if-match": current.headers()["etag"] ?? "" },
        data: { skills: [{ names: { en: "Legal translation", ar: "الترجمة القانونية" } }, { names: { en: "Excel", ar: "إكسل" } }] },
      });
      expect(saved.ok(), await saved.text()).toBe(true);
      const community = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Translators", ar: "المترجمون" }, kind: "open" } });
      expect(community.status(), await community.text()).toBe(201);
      const { id } = (await community.json()) as { id: string };
      const client = await tenantApiAs(tenant, `layla@${tenant.key}.example`);
      const layla = ((await (await client.get("/api/v1/me/profile")).json()) as { profile: { id: string } }).profile.id;
      expect((await admin.put(`/api/v1/engagement/communities/${id}/members`, { data: { userId: layla } })).ok()).toBe(true);
      const body = { blocks: [{ type: "paragraph", children: [{ text: "Tips for legal translation of tenancy contracts" }] }] };
      const post = await client.post(`/api/v1/engagement/communities/${id}/posts`, { data: { body } });
      expect(post.status(), await post.text()).toBe(201);
      await Promise.all([admin.dispose(), client.dispose()]);
    });

    test("Layla turns suggestions on, asks for them and adds one to her skills", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/people/me`);
      // The switch follows the saved answer, so it turns on once the API has it.
      const consent = page.getByLabel(m("people.suggestions.consent"));
      await consent.click();
      await expect(consent).toBeChecked();
      const run = page.getByRole("button", { name: m("people.suggestions.run") });
      await expect(run).toBeEnabled();
      await run.click();
      const list = page.getByTestId("skill-suggestions");
      await expect(list.getByText(skill, { exact: true })).toBeVisible();
      await expect(list.getByRole("list", { name: m("people.suggestions.fromFor", { skill }) }).getByRole("link")).toHaveAttribute("href", /\/communities\/.+\/posts\//);
      await expectAccessible(page, testInfo, `people-suggestions-${locale}`);

      await page.getByLabel(m("people.suggestions.levelFor", { skill })).selectOption("expert");
      await page.getByRole("button", { name: m("people.suggestions.addFor", { skill }) }).click();
      await expect(page.getByText(m("people.suggestions.noneTitle"))).toBeVisible();
      await expect(page.getByTestId("own-skills")).toContainText(skill);
      await expect(page.getByLabel(m("people.skills.levelFor", { skill }))).toHaveValue("expert");
    });
  });
}
