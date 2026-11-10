"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { SelectField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";

export type SlackChannelOption = { id: string; name: string; member: boolean };

/**
 * Shares a published item's link to a Slack channel (spec 017 US8 scenario 1). Only the title, the note and the link
 * are posted. The API decides who may share and which items may go to a channel (Public or Internal, aimed at everyone).
 */
export function SlackShare({ itemId, channels }: { itemId: string; channels: SlackChannelOption[] }) {
  const t = useTranslations("content.slack");
  const [channel, setChannel] = useState(channels.find((c) => c.member)?.id ?? channels[0]?.id ?? "");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; messages: string[] } | null>(null);

  async function share() {
    setBusy(true);
    setResult(null);
    try {
      const response = await fetch(`/api/v1/content/items/${encodeURIComponent(itemId)}/share-slack`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ channelId: channel, note: note.trim() || null }),
      });
      if (response.ok) {
        setNote("");
        setResult({ ok: true, messages: [t("shared")] });
      } else {
        setResult({ ok: false, messages: await problemMessages(response) });
      }
    } catch {
      setResult({ ok: false, messages: [t("failed")] });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="content-card" aria-labelledby="slack-share-heading">
      <h2 id="slack-share-heading">{t("title")}</h2>
      <p>{t("intro")}</p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void share();
        }}
      >
        <SelectField
          label={t("channel")}
          name="channel"
          value={channel}
          onChange={(e) => setChannel(e.target.value)}
          options={channels.map((c) => ({ value: c.id, label: c.member ? `#${c.name}` : t("notMember", { name: c.name }) }))}
        />
        <TextAreaField label={t("note")} name="note" maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        {result ? (
          <div role={result.ok ? "status" : "alert"} className={result.ok ? "notice" : "notice notice-error"}>
            {result.messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={!channel}>
          {t("share")}
        </Button>
      </form>
    </section>
  );
}
