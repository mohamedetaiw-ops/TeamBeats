"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { nameIn, type LibraryFramework, type MappedControl } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import { SelectField } from "@/components/ui/FormField";
import "./policies.css";

/**
 * Maps a policy to controls of the control library (spec 025 FR-015) for a compliance officer: pick a framework and a
 * control, add it, remove what no longer applies, and save. The API refuses any control the library does not have.
 */
export function ControlMappingEditor({ policyId, initial, library }: { policyId: string; initial: MappedControl[]; library: LibraryFramework[] }) {
  const t = useTranslations("policies.controls");
  const locale = useLocale();
  const ids = useId();
  const router = useRouter();
  const [chosen, setChosen] = useState(initial.map((c) => ({ framework: c.framework, code: c.code })));
  const [framework, setFramework] = useState(library[0]?.key ?? "");
  const set = library.find((f) => f.key === framework);
  const [code, setCode] = useState(set?.controls[0]?.code ?? "");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [status, setStatus] = useState<string | null>(null);

  const label = (f: string, c: string) => {
    const fw = library.find((x) => x.key === f);
    const control = fw?.controls.find((x) => x.code === c);
    return t("controlLabel", { framework: fw ? nameIn(fw.names, locale) : f, code: c, title: control ? nameIn(control.titles, locale) : "" });
  };

  function add() {
    if (!framework || !code || chosen.some((c) => c.framework === framework && c.code === code)) return;
    setChosen((all) => [...all, { framework, code }]);
    setStatus(null);
  }

  async function save() {
    setBusy(true);
    setMessages([]);
    setStatus(null);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/controls`, {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ controls: chosen }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      setStatus(t("saved"));
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="policies-card" aria-labelledby={`${ids}-title`} data-testid="control-editor">
      <h2 id={`${ids}-title`}>{t("editTitle")}</h2>
      <p className="policies-meta">{t("editIntro")}</p>
      <SelectField
        label={t("framework")}
        name="controlFramework"
        value={framework}
        onChange={(e) => {
          setFramework(e.target.value);
          setCode(library.find((f) => f.key === e.target.value)?.controls[0]?.code ?? "");
        }}
        options={library.map((f) => ({ value: f.key, label: t("frameworkLabel", { name: nameIn(f.names, locale), version: f.version }) }))}
      />
      <SelectField
        label={t("control")}
        name="controlCode"
        value={code}
        onChange={(e) => setCode(e.target.value)}
        options={(set?.controls ?? []).map((c) => ({ value: c.code, label: t("codeLabel", { code: c.code, title: nameIn(c.titles, locale) }) }))}
      />
      <div className="button-row">
        <Button icon="plus" onClick={add}>
          {t("add")}
        </Button>
      </div>
      {chosen.length === 0 ? (
        <p className="policies-meta">{t("noneChosen")}</p>
      ) : (
        <ul className="policies-list" data-testid="control-chosen">
          {chosen.map((c) => (
            <li key={`${c.framework}:${c.code}`} className="button-row">
              <bdi>{label(c.framework, c.code)}</bdi>
              <Button size="sm" variant="quiet" icon="trash" onClick={() => setChosen((all) => all.filter((x) => x !== c))}>
                {t("remove", { code: c.code })}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <div aria-live="polite">
        {messages.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
        {status ? (
          <p className="notice" role="status">
            {status}
          </p>
        ) : null}
      </div>
      <div className="button-row">
        <Button variant="primary" busy={busy} busyLabel={t("busy")} onClick={() => void save()}>
          {t("save")}
        </Button>
      </div>
    </section>
  );
}
