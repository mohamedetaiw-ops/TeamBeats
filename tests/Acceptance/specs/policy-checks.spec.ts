import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { expectDetailConformant } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T014 (US6): a compliance officer opens the question bank of a policy in force; a campaign asks the check;
 * the person fails once with the keyboard and sees the section to reread (never the answers), passes the second
 * attempt and attests. In English and Arabic, with axe on every screen.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy comprehension check in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let policyId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const both = (en: string, ar: string) => ({ en, ar });
    const body = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });
    const right = both("Up to 200 dirhams", "حتى 200 درهم");
    const wrong = both("Any amount", "أي مبلغ");

    async function ok(response: Awaited<ReturnType<APIRequestContext["post"]>>) {
      expect(response.ok(), `${response.url()}: ${await response.text()}`).toBe(true);
      return (await response.json()) as Record<string, unknown>;
    }

    test.beforeAll(async () => {
      tenant = await createTenant(`pq-${locale}`, locale, "workplace_plus", ["rana"]);
      const admin = await tenantApi(tenant);
      const policy = await ok(await admin.post("/api/v1/policies", { data: { code: "GIFT-01", names: both("Gifts", "الهدايا") } }));
      policyId = policy.id as string;
      const draft = await ok(await admin.put(`/api/v1/policies/${policyId}/draft`, {
        data: {
          titles: both("Gifts", "الهدايا"),
          clauses: [
            { level: 1, titles: both("Gift limits", "حدود الهدايا"), bodies: { en: body("Gifts up to 200 dirhams."), ar: body("هدايا حتى 200 درهم.") } },
            { level: 1, titles: both("Hospitality", "الضيافة"), bodies: { en: body("Declare every dinner."), ar: body("صرح بكل عشاء.") } },
          ],
        },
      }));
      await ok(await admin.post(`/api/v1/policies/${policyId}/submit`, { data: {} }));
      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      const tasks = (await (await rana.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
      const approval = tasks.find((t) => t.source === "policy_approval");
      expect(approval).toBeDefined();
      await ok(await rana.post(`/api/v1/tasks/${approval!.taskId}/decision`, { data: { outcome: "approve" } }));
      await rana.dispose();
      const clauses = (draft.clauses as { key: string }[]).map((c) => c.key);
      const question = (clause: string, prompt: { en: string; ar: string }) => ({
        clauseKey: clause, prompts: prompt, options: [{ key: "a", texts: right }, { key: "b", texts: wrong }], correct: ["a"],
      });
      await ok(await admin.put(`/api/v1/policies/${policyId}/drafts/${draft.id as string}/questions`, {
        data: { questions: [question(clauses[0]!, both("What gift may you accept?", "ما الهدية التي يمكنك قبولها؟")), question(clauses[1]!, both("Which dinner is fine?", "أي عشاء مقبول؟"))] },
      }));
      await ok(await admin.post(`/api/v1/policies/${policyId}/campaigns`, { data: { check: { passPercent: 100, attempts: 2 } } }));
      await admin.dispose();
    });

    test("read the bank, fail once, reread, pass and attest", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      const origin = hostOrigin(tenant.key);

      // The question bank is locked while the campaign asks it.
      await page.goto(`${origin}/policies/${policyId}`);
      await page.getByRole("link", { name: m("policies.manage.questions") }).click();
      await page.waitForURL(/\/questions$/);
      await expect(page.getByTestId("questions-locked")).toBeVisible();
      await expectAccessible(page, testInfo, `policy-questions-${locale}`);

      // The attest panel sends the person to the check first.
      await page.goto(`${origin}/policies/${policyId}`);
      await expect(page.getByTestId("attest-check")).toBeVisible();
      await page.getByRole("link", { name: m("policies.attest.takeCheck") }).click();
      await page.waitForURL(/\/check$/);
      await expectAccessible(page, testInfo, `policy-check-start-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/policies/[id]/check");
      await page.getByRole("button", { name: m("policies.check.start") }).click();
      await expect(page.getByTestId("check-question")).toHaveCount(2);
      await expectAccessible(page, testInfo, `policy-check-attempt-${locale}`);

      // First attempt: the gift question right and the dinner question wrong, with the keyboard.
      async function answer(dinnerRight: boolean) {
        for (const q of await page.getByTestId("check-question").all()) {
          const dinner = (await q.textContent())?.includes(locale === "ar" ? "عشاء" : "dinner");
          const option = q.getByRole("radio", { name: (dinner && !dinnerRight ? wrong : right)[locale] });
          await option.focus();
          await page.keyboard.press("Space");
          await expect(option).toBeChecked();
        }
        await page.getByRole("button", { name: m("policies.check.submit") }).click();
      }
      await answer(false);
      const result = page.getByTestId("check-result");
      await expect(result.getByRole("status")).toBeVisible();
      await expect(page.getByTestId("check-reread").getByRole("link")).toHaveCount(1);
      await expect(page.getByTestId("check-reread")).toContainText(locale === "ar" ? "الضيافة" : "Hospitality");
      await expect(result).not.toContainText(right[locale]);
      await expectAccessible(page, testInfo, `policy-check-failed-${locale}`);

      // Second attempt right: on to the attestation.
      await result.getByRole("button").click();
      await expect(page.getByTestId("check-question")).toHaveCount(2);
      await answer(true);
      await expect(result.getByRole("status")).toBeVisible();
      await result.getByRole("link", { name: m("policies.check.toAttest") }).click();
      await page.waitForURL(/\/policies\/[0-9a-f-]{36}\?attest=1$/);
      const panel = page.getByTestId("attest-panel");
      await expect(panel.getByTestId("attest-check")).toHaveCount(0);
      await panel.getByRole("checkbox").check();
      await panel.getByRole("button", { name: m("policies.attest.submit") }).click();
      await expect(panel.getByRole("status")).toBeVisible();
    });
  });
}
