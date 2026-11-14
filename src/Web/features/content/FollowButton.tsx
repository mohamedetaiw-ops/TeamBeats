"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";

/**
 * Follows or stops following a site or a topic (spec 009 FR-017). The API decides what may be followed; a refusal is
 * shown as its message.
 */
export function FollowButton({ kind, followKey, label, following: initial }: { kind: "site" | "topic"; followKey: string; label: string; following: boolean }) {
  const t = useTranslations("content.topics");
  const [following, setFollowing] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function toggle() {
    setBusy(true);
    setMessages([]);
    try {
      const response = following
        ? await fetch(`/api/v1/content/follows/${kind}/${encodeURIComponent(followKey)}`, { method: "DELETE" })
        : await fetch("/api/v1/content/follows", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ kind, key: followKey }) });
      if (response.ok) {
        setFollowing(!following);
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
    <span className="content-follow">
      <Button variant={following ? "secondary" : "primary"} aria-pressed={following} busy={busy} busyLabel={t("working")} onClick={() => void toggle()}
        aria-label={following ? t("unfollowNamed", { name: label }) : t("followNamed", { name: label })}>
        {following ? t("following") : t("follow")}
      </Button>
      {messages.length > 0 ? (
        <span role="alert" className="field-error">
          {messages.join(" ")}
        </span>
      ) : null}
    </span>
  );
}
