"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { call, send, spacesApi, visibilities, type SpaceView } from "@/lib/spaces";
import "./spaces.css";

/**
 * A space's settings (spec 029 FR-001, FR-013): name, description, visibility, joining, classification and dates for
 * owners, and archive or restore for owners and spaces administrators. An archived space is read-only until restored.
 */
export function Settings({ initial }: { initial: SpaceView }) {
  const t = useTranslations("spaces.settings");
  const router = useRouter();
  const [space, setSpace] = useState(initial);
  const [name, setName] = useState(initial.name);
  const [description, setDescription] = useState(initial.description);
  const [visibility, setVisibility] = useState<string>(initial.visibility);
  const [allowJoin, setAllowJoin] = useState(initial.allowJoin);
  const [confidential, setConfidential] = useState(initial.classification === "confidential");
  const [startsOn, setStartsOn] = useState(initial.startsOn ?? "");
  const [endsOn, setEndsOn] = useState(initial.endsOn ?? "");
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const url = `${spacesApi}/${space.id}`;
  const active = space.status === "active";

  async function save() {
    setBusy(true);
    setSaved(false);
    const result = await call<SpaceView>(
      () =>
        send(url, "PUT", {
          name,
          description,
          visibility,
          allowJoin: visibility === "public" && allowJoin,
          classification: confidential ? "confidential" : "internal",
          startsOn: space.kind === "project" && startsOn ? startsOn : null,
          endsOn: space.kind === "project" && endsOn ? endsOn : null,
          version: space.version,
        }),
      t("failed"),
    );
    setBusy(false);
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setSpace(result.value);
    setSaved(true);
    router.refresh();
  }

  async function archive(on: boolean) {
    const result = await call<SpaceView>(() => send(`${url}:${on ? "archive" : "restore"}`, "POST"), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setSpace(result.value);
    router.refresh();
  }

  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {saved ? <Alert tone="success" title={t("saved")} live /> : null}
      {space.rights.owns && active ? (
        <form
          className="spaces-form"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <FormField label={t("name")} name="name" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} required />
          <TextAreaField label={t("description")} name="description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000} />
          <RadioGroup legend={t("visibility")} name="visibility" cards value={visibility} onChange={(e) => setVisibility(e.target.value)}
            options={visibilities.map((v) => ({ value: v, label: t(`visibilityOption.${v}`), hint: t(`visibilityHint.${v}`) }))} />
          {visibility === "public" ? <Checkbox label={t("allowJoin")} checked={allowJoin} onChange={(e) => setAllowJoin(e.target.checked)} /> : null}
          <Checkbox label={t("confidential")} hint={t("confidentialHint")} checked={confidential} onChange={(e) => setConfidential(e.target.checked)} />
          {space.kind === "project" ? (
            <div className="spaces-toolbar">
              <FormField label={t("startsOn")} name="startsOn" type="date" value={startsOn} onChange={(e) => setStartsOn(e.target.value)} />
              <FormField label={t("endsOn")} name="endsOn" type="date" value={endsOn} onChange={(e) => setEndsOn(e.target.value)} />
            </div>
          ) : null}
          <div className="spaces-row">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("saving")}>
              {t("save")}
            </Button>
          </div>
        </form>
      ) : null}
      <section className="spaces-section" aria-labelledby="space-archive">
        <h2 id="space-archive">{active ? t("archiveTitle") : t("restoreTitle")}</h2>
        <p className="spaces-meta">{active ? t("archiveHint") : t("restoreHint")}</p>
        <div className="spaces-row">
          {active ? (
            <ConfirmAction label={t("archive")} question={t("archiveQuestion")} confirmLabel={t("archiveConfirm")} cancelLabel={t("cancel")} onConfirm={() => archive(true)} />
          ) : (
            <Button onClick={() => void archive(false)}>{t("restore")}</Button>
          )}
        </div>
      </section>
    </div>
  );
}
