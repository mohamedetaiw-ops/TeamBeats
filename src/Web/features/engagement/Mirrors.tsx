"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import { engagementApi, send, type MirrorTarget, type MirrorsView } from "@/lib/engagement";
import "./engagement.css";

/**
 * Mirroring a community to Slack and Teams channels (spec 023 R3 US10). Engagement administrators pick a channel per
 * target and whether the first 200 characters of each post may go with the notice. Whether the community may be
 * mirrored at all, and which targets are connected, come from the API.
 */
export function MirrorSettings({ communityId, initial }: { communityId: string; initial: MirrorsView }) {
  const t = useTranslations("engagement.mirrors");
  const [view, setView] = useState(initial);
  if (!view.eligible) {
    return <StateView kind="empty" title={t("notEligibleTitle")} message={t("notEligible")} icon="lock" />;
  }
  return (
    <div className="engagement">
      <p className="engagement-meta">{t("intro")}</p>
      <MirrorForm communityId={communityId} target="slack" view={view} onChange={setView} available={view.slackAvailable} />
      <MirrorForm communityId={communityId} target="teams" view={view} onChange={setView} available={view.teamsAvailable} />
    </div>
  );
}

function MirrorForm({
  communityId,
  target,
  view,
  available,
  onChange,
}: {
  communityId: string;
  target: MirrorTarget;
  view: MirrorsView;
  available: boolean;
  onChange: (view: MirrorsView) => void;
}) {
  const t = useTranslations("engagement.mirrors");
  const format = useFormatter();
  const current = view.mirrors.find((m) => m.target === target) ?? null;
  const [channel, setChannel] = useState(current?.channelId ?? "");
  const [excerpt, setExcerpt] = useState(current?.excerpt ?? false);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState<string | null>(null);
  const headingId = `mirror-${target}`;

  async function act(method: "PUT" | "DELETE") {
    setBusy(true);
    setMessages([]);
    setSaved(null);
    try {
      const response = await send(`${engagementApi}/communities/${communityId}/mirrors/${target}`, method, method === "PUT" ? { channelId: channel, excerpt } : undefined);
      if (response.ok) {
        onChange((await response.json()) as MirrorsView);
        setSaved(method === "PUT" ? t("saved") : t("removed"));
        if (method === "DELETE") {
          setChannel("");
          setExcerpt(false);
        }
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
    <section className="engagement-section" aria-labelledby={headingId}>
      <div className="engagement-row">
        <h2 id={headingId}>{target === "slack" ? t("slackTitle") : t("teamsTitle")}</h2>
        {current ? <Badge tone="success">{t("on")}</Badge> : <Badge tone="neutral">{t("off")}</Badge>}
      </div>
      {!available ? <p className="engagement-meta">{target === "slack" ? t("slackUnavailable") : t("teamsUnavailable")}</p> : null}
      {current?.lastFailure ? (
        <Alert tone="warning" title={t("failureTitle")}>
          {t(`failures.${current.lastFailure}`)}
          {current.lastFailureAt ? (
            <>
              {" "}
              <time dateTime={current.lastFailureAt}>{format.dateTime(new Date(current.lastFailureAt), { dateStyle: "medium", timeStyle: "short" })}</time>
            </>
          ) : null}
        </Alert>
      ) : null}
      {current?.lastSentAt ? (
        <p className="engagement-meta">
          {t("lastSent")} <time dateTime={current.lastSentAt}>{format.dateTime(new Date(current.lastSentAt), { dateStyle: "medium", timeStyle: "short" })}</time>
        </p>
      ) : null}
      <form
        className="engagement"
        onSubmit={(e) => {
          e.preventDefault();
          void act("PUT");
        }}
      >
        {target === "slack" && view.slackChannels.length > 0 ? (
          <SelectField
            label={t("slackChannel")}
            name={`mirror-${target}-channel`}
            value={channel}
            onChange={(e) => setChannel(e.target.value)}
            options={[{ value: "", label: t("choose") }, ...view.slackChannels.map((c) => ({ value: c.id, label: c.member ? `#${c.name}` : t("notMember", { name: c.name }) }))]}
          />
        ) : (
          <FormField
            label={target === "slack" ? t("slackChannel") : t("teamsChannel")}
            hint={target === "teams" ? t("teamsHint") : undefined}
            name={`mirror-${target}-channel`}
            value={channel}
            dir="ltr"
            maxLength={200}
            onChange={(e) => setChannel(e.target.value)}
          />
        )}
        <Checkbox label={t("excerpt")} hint={t("excerptHint")} checked={excerpt} onChange={(e) => setExcerpt(e.target.checked)} />
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {saved ? <Alert tone="success" title={saved} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("working")} disabled={!channel.trim()}>
            {t("save")}
          </Button>
          {current ? (
            <Button variant="quiet" icon="trash" busy={busy} busyLabel={t("working")} onClick={() => void act("DELETE")}>
              {t("remove")}
            </Button>
          ) : null}
        </div>
      </form>
    </section>
  );
}
