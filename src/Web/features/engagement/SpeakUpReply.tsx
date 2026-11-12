"use client";

import Link from "next/link";
import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { engagementApi, postHref, send, type AnonymousReply } from "@/lib/engagement";
import "./engagement.css";

/** The token from a reply link (the part after #) or from a pasted link or token. */
function tokenFrom(value: string): string {
  const hash = value.indexOf("#");
  return (hash >= 0 ? value.slice(hash + 1) : value).trim();
}

/**
 * Where an anonymous Speak Up author reads the moderator's decision (spec 023 US2 scenario 2). The token stays in the
 * part of the link after #, which browsers never send to the server, and is posted in the request body. Once a
 * decision has been shown the link stops working.
 */
export function SpeakUpReply() {
  const t = useTranslations("engagement.speakUp");
  const [pasted, setPasted] = useState("");
  const [busy, setBusy] = useState(false);
  const [reply, setReply] = useState<AnonymousReply | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [missing, setMissing] = useState(false);

  async function read(e: React.FormEvent) {
    e.preventDefault();
    const token = tokenFrom(pasted) || tokenFrom(window.location.hash);
    setMessages([]);
    setMissing(false);
    if (!token) {
      setMissing(true);
      return;
    }
    setBusy(true);
    try {
      const response = await send(`${engagementApi}/speak-up/reply`, "POST", { token });
      if (response.ok) setReply((await response.json()) as AnonymousReply);
      else if (response.status === 404) setMissing(true);
      else setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  if (reply) {
    if (reply.status === "pending") return <Alert tone="info" title={t("replyPendingTitle")}>{t("replyPending")}</Alert>;
    if (reply.status === "published")
      return (
        <Alert tone="success" title={t("replyPublishedTitle")}>
          {reply.postId ? <Link href={postHref(reply.communityId, reply.postId)}>{t("replyOpenPost")}</Link> : null}
        </Alert>
      );
    if (reply.status === "answered")
      return (
        <Alert tone="info" title={t("replyAnsweredTitle")}>
          <span dir="auto">{reply.answer}</span>
        </Alert>
      );
    return (
      <Alert tone="warning" title={t("replyDeclinedTitle")}>
        <span dir="auto">{reply.reason}</span>
      </Alert>
    );
  }
  return (
    <form className="engagement-section" onSubmit={(e) => void read(e)} aria-label={t("replyFormLabel")}>
      <p className="engagement-meta">{t("replyIntro")}</p>
      <FormField label={t("replyPaste")} name="reply-link" hint={t("replyPasteHint")} value={pasted} dir="ltr" onChange={(e) => setPasted(e.target.value)} />
      {missing ? <Alert tone="warning" title={t("replyMissingTitle")} live>{t("replyMissing")}</Alert> : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <div className="engagement-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")}>
          {t("replyRead")}
        </Button>
      </div>
    </form>
  );
}
