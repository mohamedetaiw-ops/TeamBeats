"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { libraryHref, type ConnectedSource } from "@/lib/documents/types";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import "./documents.css";

export type ScopeChoice = { value: string; label: string };

/**
 * Connects a SharePoint document library (spec 011 US9 scenario 2, FR-026): a name, the document library from an
 * allowed site, and the scope whose members are its audience. The API checks that the person manages that scope and
 * that the document library is one the connection allows.
 */
export function ConnectLibraryForm({ sources, scopes }: { sources: ConnectedSource[]; scopes: ScopeChoice[] }) {
  const t = useTranslations("documents.connect");
  const locale = useLocale();
  const router = useRouter();
  const choices = sources.flatMap((s) => s.containers.map((c) => ({ value: `${s.connectionId}|${c.id}`, label: c.name })));
  const [name, setName] = useState("");
  const [container, setContainer] = useState(choices[0]?.value ?? "");
  const [scope, setScope] = useState(scopes[0]?.value ?? "tenant");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  if (choices.length === 0) {
    return (
      <section className="documents-card" aria-labelledby="connect-library-title">
        <h2 id="connect-library-title">{t("title")}</h2>
        <p>{t(sources.some((s) => s.state !== "ok") ? "unavailable" : "noLibraries")}</p>
      </section>
    );
  }

  async function submit() {
    setBusy(true);
    setMessages([]);
    const [connectionId, containerId] = container.split("|");
    const [scopeKind, scopeId] = scope.split("|");
    try {
      const response = await fetch("/api/v1/documents/libraries/connected", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ names: { [locale]: name }, scope: scopeKind, scopeId: scopeId ?? null, connectionId, containerId }),
      });
      if (response.ok) {
        const view = (await response.json()) as { id: string };
        router.push(libraryHref(view.id));
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="documents-card" aria-labelledby="connect-library-title">
      <h2 id="connect-library-title">{t("title")}</h2>
      <p>{t("intro")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <form
        className="documents-form"
        onSubmit={(e) => {
          e.preventDefault();
          void submit();
        }}
      >
        <FormField label={t("name")} name="name" required maxLength={120} value={name} onChange={(e) => setName(e.target.value)} />
        <SelectField label={t("library")} name="container" options={choices} value={container} onChange={(e) => setContainer(e.target.value)} />
        <SelectField label={t("scope")} hint={t("scopeHint")} name="scope" options={scopes} value={scope} onChange={(e) => setScope(e.target.value)} />
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={!name.trim()}>
          {t("submit")}
        </Button>
      </form>
    </section>
  );
}
