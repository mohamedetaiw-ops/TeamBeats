"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { FormField } from "@/components/ui/FormField";

/**
 * Development sign-in (spec 003 US1, US7). "open" picks a seeded account by email; "testaccount" is a one-click
 * sign-in as the staging test account named by `testAccount`, plus the direct link for the test employee.
 */
export type DevSignInMode = "open" | "testaccount";

/**
 * Direct sign-in link for the staging test employee (spec 003 US7). The development host signs a fictional employee
 * in through the fake Microsoft page; the production API has no /dev routes, so the link grants nothing there.
 */
export const TEST_EMPLOYEE_LINK = "/dev/fake/signin/employee";

export function DevSignInForm({ mode, testAccount = "admin" }: { mode: DevSignInMode; testAccount?: string }) {
  const t = useTranslations("signin");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const body =
      mode === "testaccount" ? { username: testAccount } : { email: String(new FormData(event.currentTarget).get("email") ?? "") };
    const response = await fetch("/dev/signin", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    setBusy(false);
    if (response.ok) {
      router.push("/");
      router.refresh();
      return;
    }
    const problem = (await response.json().catch(() => null)) as { code?: string } | null;
    if (problem?.code?.startsWith("tenant.")) setError(t("unavailable"));
    else setError(mode === "testaccount" ? t("testFailed") : t("failed"));
  }

  return (
    <form onSubmit={submit} noValidate>
      <p className="notice">{mode === "testaccount" ? t("testNotice") : t("devNotice")}</p>
      {mode === "testaccount" ? (
        error ? (
          <p className="field-error" role="alert">
            {error}
          </p>
        ) : null
      ) : (
        <FormField label={t("email")} name="email" type="email" autoComplete="email" required dir="ltr" error={error} />
      )}
      <div className="button-row">
        <button className="button" type="submit" disabled={busy}>
          {mode === "testaccount" ? t("testSubmit") : t("submit")}
        </button>
        {mode === "testaccount" ? (
          <a className="button secondary" href={TEST_EMPLOYEE_LINK}>
            {t("testEmployee")}
          </a>
        ) : null}
      </div>
    </form>
  );
}
