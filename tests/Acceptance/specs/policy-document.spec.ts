import type { APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/**
 * Spec 025 T023 (FR-003): a compliance officer keeps a policy as a released controlled document. The editor takes the
 * document ID, the clauses come from its text, and readers see the policy with the link to the signed document. In
 * English and Arabic, with axe.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`policy kept as a controlled document in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let policyId: string;
    let documentId: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const text =
      locale === "ar"
        ? "الأجهزة\nاقفل شاشتك عند المغادرة.\n\nكلمات المرور\nلا تشارك كلمة المرور."
        : "Devices\nLock your screen when you leave.\n\nPasswords\nNever share your password.";

    async function ok(response: Awaited<ReturnType<APIRequestContext["post"]>>) {
      expect(response.ok(), `${response.url()}: ${await response.text()}`).toBe(true);
      return (await response.json()) as Record<string, unknown>;
    }

    async function decide(api: APIRequestContext, source: string) {
      const tasks = (await (await api.get("/api/v1/tasks")).json()) as { source: string; taskId: string }[];
      const task = tasks.find((t) => t.source === source);
      expect(task, source).toBeDefined();
      await ok(await api.post(`/api/v1/tasks/${task!.taskId}/decision`, { data: { outcome: "approve" } }));
    }

    test.beforeAll(async () => {
      tenant = await createTenant(`pdo-${locale}`, locale, "enterprise_gov", ["rana"]);
      const admin = await tenantApi(tenant);
      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      const library = await ok(await admin.post("/api/v1/documents/libraries", { data: { names: { [locale]: "Policies" }, scope: "tenant" } }));
      const type = await ok(
        await admin.post(`/api/v1/documents/libraries/${library.id as string}/types`, {
          data: {
            names: { [locale]: "Policy" },
            fields: [],
            controlled: { numberPrefix: "pol-", numberDigits: 3, readConfirmation: false, reviewMonths: 12, steps: [{ approvers: [(await (await rana.get("/api/v1/me")).json()).id] }] },
          },
        }),
      );
      const doc = await ok(
        await admin.post(`/api/v1/documents/libraries/${library.id as string}/files?name=use.txt&type=${type.id as string}&publish=false`, {
          data: Buffer.from(text, "utf8"),
          headers: { "content-type": "text/plain" },
        }),
      );
      documentId = doc.id as string;
      await ok(await admin.post(`/api/v1/documents/${documentId}/release`, { data: {} }));
      await decide(rana, "document_release");
      const policy = await ok(await admin.post("/api/v1/policies", { data: { code: "AUP-01", names: { en: "Acceptable use", ar: "الاستخدام المقبول" } } }));
      policyId = policy.id as string;
      await rana.dispose();
      await admin.dispose();
    });

    test("keep the policy as a controlled document and read it", async ({ page }, testInfo) => {
      const origin = hostOrigin(tenant.key);
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${origin}/policies/${policyId}/edit`);
      await page.getByLabel(m("policies.editor.source.document")).check();
      await page.getByLabel(m("policies.editor.source.reference")).fill(`${origin}/documents/${documentId}`);
      await page.getByLabel(m("policies.editor.title")).fill(locale === "ar" ? "الاستخدام المقبول" : "Acceptable use");
      await page.getByRole("button", { name: m("policies.editor.save") }).click();
      const linked = page.getByTestId("policy-document-source");
      await expect(linked).toContainText("POL-001");
      await expectAccessible(page, testInfo, `policy-document-${locale}`);
      await page.getByRole("button", { name: m("policies.editor.submit") }).click();
      await expect(page).toHaveURL(new RegExp(`/policies/${policyId}$`));

      const rana = await tenantApiAs(tenant, `rana@${tenant.key}.example`);
      await decide(rana, "policy_approval");
      await rana.dispose();
      await page.reload();
      const note = page.getByTestId("policy-controlled-document");
      await expect(note).toContainText("POL-001");
      await expect(note.getByRole("link", { name: m("policies.reading.openDocument") })).toHaveAttribute("href", `/documents/${documentId}`);
      await expect(page.getByTestId("policy-document")).toContainText(locale === "ar" ? "كلمات المرور" : "Passwords");
      await expectAccessible(page, testInfo, `policy-document-read-${locale}`);
    });
  });
}
