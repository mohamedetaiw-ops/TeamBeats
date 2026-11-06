"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { FormField } from "@/components/ui/FormField";

/** Development sign-in (spec 003 US1, US7). "open" picks a seeded account by email; "password" is the staging test account. */
export type DevSignInMode = "open" | "password";

export function DevSignInForm({ mode }: { mode: DevSignInMode }) {
  const t = useTranslations("signin");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const form = new FormData(event.currentTarget);
    const body =
      mode === "password"
        ? { username: String(form.get("username") ?? ""), password: String(form.get("password") ?? "") }
        : { email: String(form.get("email") ?? "") };
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
    else setError(mode === "password" ? t("testFailed") : t("failed"));
  }

  return (
    <form onSubmit={submit} noValidate>
      <p className="notice">{mode === "password" ? t("testNotice") : t("devNotice")}</p>
      {mode === "password" ? (
        <>
          <FormField label={t("username")} name="username" autoComplete="username" required dir="ltr" />
          <FormField label={t("password")} name="password" type="password" autoComplete="current-password" required dir="ltr" error={error} />
        </>
      ) : (
        <FormField label={t("email")} name="email" type="email" autoComplete="email" required dir="ltr" error={error} />
      )}
      <button className="button" type="submit" disabled={busy}>
        {t("submit")}
      </button>
    </form>
  );
}
