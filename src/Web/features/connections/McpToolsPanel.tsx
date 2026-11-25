"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { MCP_TOOL_STATUSES, TOOL_SIDE_EFFECTS, known, type McpRefreshResult, type McpTool } from "@/lib/integrations/types";
import { Messages } from "./parts";
import { send } from "./request";

/** The name agents see: the approved name under the mcp prefix the API gives every MCP tool. */
const agentToolName = (name: string) => `mcp.${name}`;

const TONES: Record<(typeof MCP_TOOL_STATUSES)[number], BadgeTone> = { pending: "warning", approved: "success", rejected: "neutral", unsupported: "danger" };

/**
 * The tools of an external MCP server (spec 027 R3, FR-006). A refresh reads the server's tool list; each new or changed
 * tool waits until the integration administrator approves it with a name, a side effect and titles, or rejects it. The
 * server's own description is shown as it is, because agents read it. Every rule is the API's.
 */
export function McpToolsPanel({ connectionId, initial }: { connectionId: string; initial: McpTool[] | null }) {
  const t = useTranslations("connections");
  const locale = useLocale();
  const sideId = useId();
  const returnTo = `/admin/connections/${connectionId}`;
  const base = `/api/v1/integrations/pack-connections/${connectionId}/mcp-tools`;
  const [tools, setTools] = useState<McpTool[]>(initial ?? []);
  const [open, setOpen] = useState<string | null>(null);
  const [name, setName] = useState("");
  const [sideEffect, setSideEffect] = useState<string>("read");
  const [titleEn, setTitleEn] = useState("");
  const [titleAr, setTitleAr] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ ok: boolean; list: string[] }>({ ok: true, list: [] });
  const title = (tool: McpTool) => (tool.title ? (tool.title[locale] ?? Object.values(tool.title)[0]) : null) ?? tool.remoteName;
  const replace = (tool: McpTool) => setTools(tools.map((x) => (x.id === tool.id ? tool : x)));

  function review(tool: McpTool) {
    setOpen(tool.id);
    setName(tool.name ?? tool.remoteName.toLowerCase().replace(/[^a-z0-9_]/g, "_").replace(/^[^a-z]+/, "").slice(0, 41));
    setSideEffect(tool.sideEffect ?? (tool.readOnlyHint ? "read" : "write_external"));
    setTitleEn(tool.title?.en ?? "");
    setTitleAr(tool.title?.ar ?? "");
    setMessages({ ok: true, list: [] });
  }

  async function refresh() {
    setBusy("refresh");
    setMessages({ ok: true, list: [] });
    const result = await send<McpRefreshResult>("POST", `${base}:refresh`, undefined, returnTo, t("detail.failed"));
    if (result.ok) {
      setTools(result.data.tools);
      setMessages({
        ok: true,
        list: [t("mcpTools.refreshed", { listed: String(result.data.listed), new: String(result.data.new), changed: String(result.data.changed), gone: String(result.data.gone) })],
      });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  async function approve(tool: McpTool) {
    setBusy(`approve-${tool.id}`);
    setMessages({ ok: true, list: [] });
    const result = await send<McpTool>("POST", `${base}/${tool.id}:approve`, { name: name.trim(), sideEffect, title: { en: titleEn, ar: titleAr } }, returnTo, t("detail.failed"));
    if (result.ok) {
      replace(result.data);
      setOpen(null);
      setMessages({ ok: true, list: [t("mcpTools.approved")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  async function reject(tool: McpTool) {
    setBusy(`reject-${tool.id}`);
    setMessages({ ok: true, list: [] });
    const result = await send<McpTool>("POST", `${base}/${tool.id}:reject`, undefined, returnTo, t("detail.failed"));
    if (result.ok) {
      replace(result.data);
      setOpen(null);
      setMessages({ ok: true, list: [t("mcpTools.rejected")] });
    } else {
      setMessages({ ok: false, list: result.messages });
    }
    setBusy(null);
  }

  return (
    <section className="connections-section" aria-labelledby="mcp-tools-heading">
      <h2 id="mcp-tools-heading">{t("mcpTools.title")}</h2>
      <p>{t("mcpTools.intro")}</p>
      <div className="connections-row">
        <Button type="button" variant="secondary" busy={busy === "refresh"} busyLabel={t("detail.working")} onClick={() => void refresh()}>
          {t("mcpTools.refresh")}
        </Button>
      </div>
      {initial === null ? (
        <p className="notice">{t("mcpTools.loadFailed")}</p>
      ) : tools.length === 0 ? (
        <p className="notice">{t("mcpTools.empty")}</p>
      ) : (
        <ul className="connections-list">
          {tools.map((tool) => (
            <li key={tool.id} className="connections-item">
              <h3>
                <bdi>{title(tool)}</bdi>
              </h3>
              <p className="connections-row">
                <Badge tone={known(MCP_TOOL_STATUSES, tool.status) ? TONES[tool.status] : "neutral"}>
                  {known(MCP_TOOL_STATUSES, tool.status) ? t(`mcpTools.statuses.${tool.status}`) : tool.status}
                </Badge>
                {tool.changed ? <Badge tone="info">{t("mcpTools.changed")}</Badge> : null}
                {!tool.listed ? <Badge tone="neutral">{t("mcpTools.gone")}</Badge> : null}
                <span>
                  {t("mcpTools.remoteName")} <bdi dir="ltr">{tool.remoteName}</bdi>
                </span>
                {tool.name ? (
                  <span>
                    {t("mcpTools.agentName")} <bdi dir="ltr">{agentToolName(tool.name)}</bdi>
                  </span>
                ) : null}
                {tool.sideEffect && known(TOOL_SIDE_EFFECTS, tool.sideEffect) ? <span>{t(`tools.sideEffects.${tool.sideEffect}`)}</span> : null}
              </p>
              <p dir="ltr">
                <bdi>{tool.description || t("mcpTools.noDescription")}</bdi>
              </p>
              {tool.status === "unsupported" ? <p className="field-hint">{t("mcpTools.unsupportedNote")}</p> : null}
              {tool.status !== "unsupported" && tool.listed && open !== tool.id ? (
                <div className="connections-row">
                  <Button type="button" variant="secondary" onClick={() => review(tool)}>
                    {tool.status === "approved" ? t("mcpTools.edit") : t("mcpTools.review")}
                  </Button>
                  {tool.status !== "rejected" ? (
                    <ConfirmAction
                      label={t("mcpTools.reject")}
                      question={t("mcpTools.rejectQuestion", { name: title(tool) })}
                      confirmLabel={t("mcpTools.rejectConfirm")}
                      cancelLabel={t("detail.cancel")}
                      onConfirm={() => reject(tool)}
                    />
                  ) : null}
                </div>
              ) : null}
              {open === tool.id ? (
                <form
                  className="connections-form"
                  aria-label={t("mcpTools.approveTitle", { name: tool.remoteName })}
                  onSubmit={(e) => {
                    e.preventDefault();
                    void approve(tool);
                  }}
                >
                  <FormField label={t("tools.name")} hint={t("mcpTools.nameHint")} name="mcpToolName" dir="ltr" required maxLength={41} value={name} onChange={(e) => setName(e.target.value)} />
                  <div className="field">
                    <label htmlFor={sideId}>{t("mcpTools.sideEffect")}</label>
                    <select id={sideId} value={sideEffect} onChange={(e) => setSideEffect(e.target.value)}>
                      {TOOL_SIDE_EFFECTS.map((s) => (
                        <option key={s} value={s}>
                          {t(`tools.sideEffects.${s}`)}
                        </option>
                      ))}
                    </select>
                    <p className="field-hint">{tool.readOnlyHint ? t("mcpTools.readOnlyHint") : t("mcpTools.noReadOnlyHint")}</p>
                  </div>
                  <FormField label={t("tools.titleEn")} name="mcpToolTitleEn" dir="ltr" maxLength={100} value={titleEn} onChange={(e) => setTitleEn(e.target.value)} />
                  <FormField label={t("tools.titleAr")} name="mcpToolTitleAr" dir="rtl" maxLength={100} value={titleAr} onChange={(e) => setTitleAr(e.target.value)} />
                  <p className="field-hint">{t("mcpTools.approveNote")}</p>
                  <div className="connections-row">
                    <Button type="submit" variant="primary" busy={busy === `approve-${tool.id}`} busyLabel={t("detail.working")}>
                      {t("mcpTools.approve")}
                    </Button>
                    <Button type="button" variant="secondary" onClick={() => setOpen(null)}>
                      {t("detail.cancel")}
                    </Button>
                  </div>
                </form>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      <Messages messages={messages.list} ok={messages.ok} />
    </section>
  );
}
