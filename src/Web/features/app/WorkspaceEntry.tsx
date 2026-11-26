"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { inShell, shellPlatform } from "@/lib/mobile/shell";
import "./app.css";

/** A workspace address as typed: a plain https host name with a dot, or null. The shell checks it again. */
function host(input: string): string | null {
  const text = input.trim().toLowerCase().replace(/^https:\/\//, "").split(/[/?#]/, 1)[0]?.replace(/\.$/, "") ?? "";
  return /^(?=.{4,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/.test(text) ? text : null;
}

/**
 * The workspace address form of the store app's start page. In the app it hands the address back to the shell, which
 * asks that workspace whether it takes the app; in a browser it opens the workspace's sign-in.
 */
export function WorkspaceEntry({ problem }: { problem: string | null }) {
  const t = useTranslations("app.start");
  const [value, setValue] = useState("");
  const [error, setError] = useState<string | null>(null);

  function open() {
    const workspace = host(value);
    if (!workspace) {
      setError(t("invalid"));
      return;
    }
    const local = shellPlatform() === "ios" ? "capacitor://localhost" : "https://localhost";
    window.location.assign(inShell() ? `${local}/?workspace=${encodeURIComponent(workspace)}` : `https://${workspace}/signin`);
  }

  return (
    <form
      className="app-start-form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        open();
      }}
    >
      {problem ? <Alert tone="warning" title={t("problemTitle")}>
          {problem}
        </Alert> : null}
      <FormField
        label={t("workspace")}
        hint={t("workspaceHint")}
        name="workspace"
        dir="ltr"
        inputMode="url"
        autoCapitalize="none"
        autoCorrect="off"
        autoComplete="url"
        value={value}
        error={error}
        onChange={(e) => {
          setValue(e.target.value);
          setError(null);
        }}
      />
      <Button type="submit" variant="primary">
        {t("open")}
      </Button>
    </form>
  );
}
