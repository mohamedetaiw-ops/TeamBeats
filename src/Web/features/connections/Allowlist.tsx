"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import type { AllowlistEntry } from "@/lib/integrations/types";
import { Messages } from "./parts";
import { send } from "./request";

/** The hosts outbound calls may reach (spec 017 FR-012). Exact names or one leading wildcard; the API checks each. */
export function Allowlist({ initial, returnTo }: { initial: AllowlistEntry[] | null; returnTo: string }) {
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
    const result = await send<AllowlistEntry>("POST", "/api/v1/integrations/allowlist", { host }, returnTo, t("allowlist.failed"));
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
    const result = await send<null>("DELETE", `/api/v1/integrations/allowlist/${encodeURIComponent(name)}`, undefined, returnTo, t("allowlist.failed"));
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
