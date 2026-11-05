"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { FormField } from "@/components/ui/FormField";

export function DevSignInForm() {
  const t = useTranslations("signin");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const email = String(new FormData(event.currentTarget).get("email") ?? "");
    const response = await fetch("/dev/signin", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ email }),
    });
    setBusy(false);
    if (response.ok) {
      router.push("/");
      router.refresh();
      return;
    }
    const problem = (await response.json().catch(() => null)) as { code?: string } | null;
    setError(problem?.code?.startsWith("tenant.") ? t("unavailable") : t("failed"));
  }

  return (
    <form onSubmit={submit} noValidate>
      <p className="notice">{t("devNotice")}</p>
      <FormField label={t("email")} name="email" type="email" autoComplete="email" required dir="ltr" error={error} />
      <button className="button" type="submit" disabled={busy}>
        {t("submit")}
      </button>
    </form>
  );
}
