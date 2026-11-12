"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { call, send, spaceHref, spacesApi, visibilities, type SpaceView, type TemplateOption } from "@/lib/spaces";
import "./spaces.css";

/**
 * A new space from a template (spec 029 US1 scenario 1). The template gives the kind, lists, columns and starter tasks;
 * the API checks who may create spaces, the package limit and every field, and the creator becomes the owner.
 */
export function CreateSpace({ templates }: { templates: TemplateOption[] }) {
  const t = useTranslations("spaces.create");
  const router = useRouter();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [template, setTemplate] = useState(templates[0]?.key ?? "project");
  const [visibility, setVisibility] = useState("private");
  const [allowJoin, setAllowJoin] = useState(false);
  const [startsOn, setStartsOn] = useState("");
  const [endsOn, setEndsOn] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const kind = templates.find((o) => o.key === template)?.kind ?? "project";

  async function create() {
    setBusy(true);
    setMessages([]);
    const result = await call<SpaceView>(
      () =>
        send(spacesApi, "POST", {
          name,
          description,
          template,
          visibility,
          allowJoin: visibility === "public" && allowJoin,
          startsOn: kind === "project" && startsOn ? startsOn : null,
          endsOn: kind === "project" && endsOn ? endsOn : null,
          members: [],
        }),
      t("failed"),
    );
    setBusy(false);
    if (result.ok) router.push(spaceHref(result.value.id));
    else setMessages(result.messages);
  }

  return (
    <form
      className="spaces-form"
      onSubmit={(e) => {
        e.preventDefault();
        void create();
      }}
    >
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <FormField label={t("name")} name="name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={120} />
      <TextAreaField label={t("description")} name="description" value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000} />
      <SelectField
        label={t("template")}
        hint={t("templateHint")}
        name="template"
        value={template}
        onChange={(e) => setTemplate(e.target.value)}
        options={templates.map((o) => ({ value: o.key, label: o.builtIn ? t("builtIn", { name: o.name }) : o.name }))}
      />
      <RadioGroup
        legend={t("visibility")}
        name="visibility"
        cards
        value={visibility}
        onChange={(e) => setVisibility(e.target.value)}
        options={visibilities.map((v) => ({ value: v, label: t(`visibilityOption.${v}`), hint: t(`visibilityHint.${v}`) }))}
      />
      {visibility === "public" ? (
        <Checkbox label={t("allowJoin")} hint={t("allowJoinHint")} checked={allowJoin} onChange={(e) => setAllowJoin(e.target.checked)} />
      ) : null}
      {kind === "project" ? (
        <div className="spaces-toolbar">
          <FormField label={t("startsOn")} name="startsOn" type="date" value={startsOn} onChange={(e) => setStartsOn(e.target.value)} />
          <FormField label={t("endsOn")} name="endsOn" type="date" value={endsOn} onChange={(e) => setEndsOn(e.target.value)} />
        </div>
      ) : null}
      <div className="spaces-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("creating")}>
          {t("submit")}
        </Button>
      </div>
    </form>
  );
}
