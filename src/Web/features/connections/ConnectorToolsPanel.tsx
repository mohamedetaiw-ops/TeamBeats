"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { TOOL_RULES, TOOL_SIDE_EFFECTS, known, type ConnectorTool, type PackAction } from "@/lib/integrations/types";
import { Messages } from "./parts";
import { send } from "./request";

/**
 * Connector actions as agent tools (spec 017 T009, spec 027 FR-006): the actions of this connection that declare their
 * inputs can be registered as tools, with a description for the model and a title per language for people. Which
 * agents may hold a tool is the AI policy's decision; the API checks every rule.
 */
export function ConnectorToolsPanel({ connectionId, actions, tools: initialTools }: { connectionId: string; actions: PackAction[]; tools: ConnectorTool[] }) {
  const t = useTranslations("connections");
  const locale = useLocale();
  const actionId = useId();
  const returnTo = `/admin/connections/${connectionId}`;
  const base = `/api/v1/integrations/pack-connections/${connectionId}/tools`;
  const usable = actions.filter((a) => a.inputs !== null && a.inputs !== undefined);
  const [tools, setTools] = useState(initialTools);
  const [chosen, setChosen] = useState("");
  // The list can grow after the page refreshes (a new action), so the first usable action is the default until one is chosen.
  const action = usable.some((a) => a.name === chosen) ? chosen : (usable[0]?.name ?? "");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [titleEn, setTitleEn] = useState("");
  const [titleAr, setTitleAr] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const title = (tool: ConnectorTool) => tool.title[locale] ?? Object.values(tool.title)[0] ?? tool.name;

  async function register() {
    setBusy("register");
    setMessages({ ok: true, list: [] });
    const result = await send<ConnectorTool>("POST", base, { action, name: name.trim(), description: description.trim(), title: { en: titleEn, ar: titleAr } }, returnTo, t("detail.failed"));
    if (result.ok) {
      setTools([...tools, result.data].sort((a, b) => a.name.localeCompare(b.name)));
      setName("");
      setDescription("");
      setTitleEn("");
      setTitleAr("");
      setMessages({ ok: true, list: [t("tools.registered")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  async function remove(tool: ConnectorTool) {
    setBusy(`remove-${tool.id}`);
    setMessages({ ok: true, list: [] });
    const result = await send<null>("DELETE", `${base}/${tool.id}`, undefined, returnTo, t("detail.failed"));
    if (result.ok) {
      setTools(tools.filter((x) => x.id !== tool.id));
      setMessages({ ok: true, list: [t("tools.removed")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  return (
    <section className="connections-section" aria-labelledby="tools-heading">
      <h2 id="tools-heading">{t("tools.title")}</h2>
      <p>{t("tools.intro")}</p>
      {tools.length === 0 ? (
        <p className="notice">{t("tools.empty")}</p>
      ) : (
        <ul className="connections-list">
          {tools.map((tool) => (
            <li key={tool.id} className="connections-item">
              <h3>
                <bdi>{title(tool)}</bdi>
              </h3>
              <p className="connections-row">
                <span>
                  {t("tools.toolName")} <bdi dir="ltr">{tool.name}</bdi>
                </span>
                <span>
                  {t("tools.action")} <bdi dir="ltr">{tool.action}</bdi>
                </span>
                <span>{known(TOOL_SIDE_EFFECTS, tool.sideEffect) ? t(`tools.sideEffects.${tool.sideEffect}`) : tool.sideEffect}</span>
                <span>{known(TOOL_RULES, tool.approvalRule) ? t(`tools.rules.${tool.approvalRule}`) : tool.approvalRule}</span>
              </p>
              <p>
                <bdi>{tool.description}</bdi>
              </p>
              <ConfirmAction
                label={t("tools.remove")}
                question={t("tools.removeQuestion", { name: title(tool) })}
                confirmLabel={t("tools.removeConfirm")}
                cancelLabel={t("detail.cancel")}
                onConfirm={() => remove(tool)}
              />
            </li>
          ))}
        </ul>
      )}

      <h3>{t("tools.registerTitle")}</h3>
      {usable.length === 0 ? (
        <p className="notice">{t("tools.noActions")}</p>
      ) : (
        <form
          className="connections-form"
          aria-label={t("tools.registerTitle")}
          onSubmit={(e) => {
            e.preventDefault();
            void register();
          }}
        >
          <div className="field">
            <label htmlFor={actionId}>{t("tools.action")}</label>
            <select id={actionId} value={action} onChange={(e) => setChosen(e.target.value)}>
              {usable.map((a) => (
                <option key={a.name} value={a.name}>
                  {a.name}
                </option>
              ))}
            </select>
          </div>
          <FormField label={t("tools.name")} hint={t("tools.nameHint")} name="toolName" dir="ltr" required maxLength={41} value={name} onChange={(e) => setName(e.target.value)} />
          <FormField label={t("tools.description")} hint={t("tools.descriptionHint")} name="toolDescription" required maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
          <FormField label={t("tools.titleEn")} name="toolTitleEn" dir="ltr" maxLength={100} value={titleEn} onChange={(e) => setTitleEn(e.target.value)} />
          <FormField label={t("tools.titleAr")} name="toolTitleAr" dir="rtl" maxLength={100} value={titleAr} onChange={(e) => setTitleAr(e.target.value)} />
          <p className="field-hint">{t("tools.policyNote")}</p>
          <div className="connections-row">
            <Button type="submit" variant="primary" busy={busy === "register"} busyLabel={t("detail.working")}>
              {t("tools.register")}
            </Button>
          </div>
        </form>
      )}
      <Messages messages={messages.list} ok={messages.ok} />
    </section>
  );
}
