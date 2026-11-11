"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { PACK_KINDS, TICKETING_KINDS, isTicketing, known, type AllowlistEntry, type Pack, type PackConnection, type WebhookEventChoice } from "@/lib/integrations/types";
import { ConnectionSettingsForm, type SettingsBody } from "./ConnectionSettingsForm";
import { OnPremSettingsForm, type OnPremBody } from "./OnPremSettingsForm";
import { ClockSettingsForm, type ClockBody } from "./ClockSettingsForm";
import { SlackSettingsForm, type SlackBody } from "./SlackSettingsForm";
import { WebhookSettingsForm, type WebhookBody } from "./WebhookSettingsForm";
import { Messages, StatusBadge } from "./parts";
import { send } from "./request";

const RETURN_TO = "/admin/connections";

/** The hosts outbound calls may reach (spec 017 FR-012). Exact names or one leading wildcard; the API checks each. */
function Allowlist({ initial }: { initial: AllowlistEntry[] | null }) {
  const t = useTranslations("connections");
  const [entries, setEntries] = useState(initial ?? []);
  const [host, setHost] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);

  if (initial === null) {
    return <p className="notice">{t("allowlist.unavailable")}</p>;
  }

  async function add() {
    setBusy("add");
    setMessages([]);
    const result = await send<AllowlistEntry>("POST", "/api/v1/integrations/allowlist", { host }, RETURN_TO, t("allowlist.failed"));
    setBusy(null);
    if (result.ok) {
      setEntries((list) => [...list.filter((e) => e.host !== result.data.host), result.data].sort((x, y) => (x.host < y.host ? -1 : 1)));
      setHost("");
    } else {
      setMessages(result.messages);
    }
  }

  async function remove(name: string) {
    setBusy(name);
    setMessages([]);
    const result = await send<null>("DELETE", `/api/v1/integrations/allowlist/${encodeURIComponent(name)}`, undefined, RETURN_TO, t("allowlist.failed"));
    setBusy(null);
    if (result.ok) setEntries((list) => list.filter((e) => e.host !== name));
    else setMessages(result.messages);
  }

  return (
    <section className="connections-section" aria-labelledby="allowlist-heading">
      <h2 id="allowlist-heading">{t("allowlist.title")}</h2>
      <p>{t("allowlist.intro")}</p>
      {entries.length === 0 ? (
        <p className="notice">{t("allowlist.empty")}</p>
      ) : (
        <ul className="connections-list">
          {entries.map((e) => (
            <li key={e.host} className="connections-row">
              <bdi dir="ltr">{e.host}</bdi>
              <Button variant="quiet" busy={busy === e.host} busyLabel={t("allowlist.working")} onClick={() => void remove(e.host)} aria-label={t("allowlist.removeHost", { host: e.host })}>
                {t("allowlist.remove")}
              </Button>
            </li>
          ))}
        </ul>
      )}
      <form
        className="connections-row"
        onSubmit={(e) => {
          e.preventDefault();
          void add();
        }}
      >
        <FormField label={t("allowlist.host")} hint={t("allowlist.hostHint")} name="host" dir="ltr" required value={host} onChange={(e) => setHost(e.target.value)} />
        <Button type="submit" busy={busy === "add"} busyLabel={t("allowlist.working")}>
          {t("allowlist.add")}
        </Button>
      </form>
      <Messages messages={messages} />
    </section>
  );
}

/**
 * The Connections area for connector packs (spec 017 US1, US4): connections with their status, the pack catalogue
 * with each pack's actions, adding a custom REST, ServiceNow or Jira Service Management connection (each starts
 * disabled), and the host allowlist.
 */
export function ConnectionList({
  initial,
  packs,
  allowlist,
  webhookEvents,
}: {
  initial: PackConnection[];
  packs: Pack[];
  allowlist: AllowlistEntry[] | null;
  webhookEvents: WebhookEventChoice[];
}) {
  const t = useTranslations("connections");
  const format = useFormatter();
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<{ kind: string; list: string[] }>({ kind: "", list: [] });
  const rest = packs.find((p) => p.kind === "custom_rest");
  const webhook = packs.find((p) => p.kind === "webhook");
  const onprem = packs.find((p) => p.kind === "onprem_ad");
  const ticketing = packs.filter((p) => p.inPackage && isTicketing(p.kind));
  const slack = packs.find((p) => p.kind === "slack");
  const clock = packs.find((p) => p.kind === "clock_import");

  async function create(kind: string, body: SettingsBody | WebhookBody | OnPremBody | SlackBody | ClockBody) {
    setBusy(kind);
    setMessages({ kind, list: [] });
    const result = await send<PackConnection>("POST", "/api/v1/integrations/pack-connections", { kind, ...body }, RETURN_TO, t("failed"));
    if (result.ok) {
      router.push(`/admin/connections/${result.data.id}`);
      return;
    }
    setBusy(null);
    setMessages({ kind, list: result.messages });
  }

  return (
    <div className="connections">
      <section className="connections-section" aria-labelledby="connections-heading">
        <h2 id="connections-heading">{t("list.title")}</h2>
        {initial.length === 0 ? (
          <p className="notice">{t("list.empty")}</p>
        ) : (
          <ul className="connections-list">
            {initial.map((c) => (
              <li key={c.id} className="connections-item">
                <h3>
                  <Link href={`/admin/connections/${c.id}`}>
                    <bdi>{c.displayName}</bdi>
                  </Link>
                </h3>
                <p className="connections-row">
                  <StatusBadge status={c.status} />
                  <span>{t(`kinds.${known(PACK_KINDS, c.kind) ? c.kind : "other"}`)}</span>
                  <span>{t("list.actions", { count: c.actions.length })}</span>
                  {c.lastSuccessAt ? <span>{t("list.lastSuccess", { at: format.dateTime(new Date(c.lastSuccessAt), { dateStyle: "medium", timeStyle: "short" }) })}</span> : null}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="connections-section" aria-labelledby="catalogue-heading">
        <h2 id="catalogue-heading">{t("catalogue.title")}</h2>
        <ul className="connections-list">
          {packs.map((p) => (
            <li key={p.kind} className="connections-item">
              <h3>{known(PACK_KINDS, p.kind) ? t(`kinds.${p.kind}`) : <bdi dir="ltr">{p.kind}</bdi>}</h3>
              <p>{t(p.inPackage ? "catalogue.inPackage" : "catalogue.notInPackage")}</p>
              {p.kind === "custom_rest" ? (
                <p>{t("catalogue.customRest")}</p>
              ) : p.kind === "webhook" ? (
                <p>{t("catalogue.webhook")}</p>
              ) : p.kind === "onprem_ad" ? (
                <p>{t("catalogue.onprem_ad")}</p>
              ) : p.kind === "slack" ? (
                <p>{t("catalogue.slack")}</p>
              ) : p.kind === "clock_import" ? (
                <p>{t("catalogue.clock_import")}</p>
              ) : (
                <p>{t("catalogue.actions", { names: p.actions.map((a) => a.name).join(", ") })}</p>
              )}
            </li>
          ))}
        </ul>
      </section>

      {rest?.inPackage ? (
        <section className="connections-section" aria-labelledby="add-heading">
          <h2 id="add-heading">{t("add.title")}</h2>
          <p>{t("add.intro")}</p>
          <ConnectionSettingsForm submitLabel={t("add.submit")} busy={busy === "custom_rest"} onSubmit={(body) => void create("custom_rest", body)} />
          {messages.kind === "custom_rest" ? <Messages messages={messages.list} /> : null}
        </section>
      ) : null}

      {ticketing.map((p) => {
        const kind = p.kind as keyof typeof TICKETING_KINDS;
        return (
          <section key={kind} className="connections-section" aria-labelledby={`add-${kind}-heading`}>
            <h2 id={`add-${kind}-heading`}>{t(`ticketing.${kind}.addTitle`)}</h2>
            <p>{t(`ticketing.${kind}.addIntro`)}</p>
            <ConnectionSettingsForm
              authTypes={TICKETING_KINDS[kind].auth}
              desk={TICKETING_KINDS[kind].desk}
              submitLabel={t("add.submit")}
              busy={busy === kind}
              onSubmit={(body) => void create(kind, body)}
            />
            {messages.kind === kind ? <Messages messages={messages.list} /> : null}
          </section>
        );
      })}

      {slack?.inPackage ? (
        <section className="connections-section" aria-labelledby="add-slack-heading">
          <h2 id="add-slack-heading">{t("slack.addTitle")}</h2>
          <p>{t("slack.addIntro")}</p>
          <SlackSettingsForm submitLabel={t("add.submit")} busy={busy === "slack"} onSubmit={(body) => void create("slack", body)} />
          {messages.kind === "slack" ? <Messages messages={messages.list} /> : null}
        </section>
      ) : null}

      {clock?.inPackage ? (
        <section className="connections-section" aria-labelledby="add-clock-heading">
          <h2 id="add-clock-heading">{t("clock.addTitle")}</h2>
          <p>{t("clock.addIntro")}</p>
          <ClockSettingsForm submitLabel={t("add.submit")} busy={busy === "clock_import"} onSubmit={(body) => void create("clock_import", body)} />
          {messages.kind === "clock_import" ? <Messages messages={messages.list} /> : null}
        </section>
      ) : null}

      {webhook?.inPackage ? (
        <section className="connections-section" aria-labelledby="add-webhook-heading">
          <h2 id="add-webhook-heading">{t("webhooks.addTitle")}</h2>
          <p>{t("webhooks.addIntro")}</p>
          <WebhookSettingsForm events={webhookEvents} submitLabel={t("webhooks.add")} busy={busy === "webhook"} onSubmit={(body) => void create("webhook", body)} />
          {messages.kind === "webhook" ? <Messages messages={messages.list} /> : null}
        </section>
      ) : null}

      {onprem?.inPackage ? (
        <section className="connections-section" aria-labelledby="add-onprem-heading">
          <h2 id="add-onprem-heading">{t("onprem.addTitle")}</h2>
          <p>{t("onprem.addIntro")}</p>
          <OnPremSettingsForm submitLabel={t("onprem.add")} busy={busy === "onprem_ad"} onSubmit={(body) => void create("onprem_ad", body)} />
          {messages.kind === "onprem_ad" ? <Messages messages={messages.list} /> : null}
        </section>
      ) : null}

      <Allowlist initial={allowlist} />
    </div>
  );
}
