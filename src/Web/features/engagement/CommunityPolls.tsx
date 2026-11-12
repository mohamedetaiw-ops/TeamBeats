"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { StateView } from "@/components/ui/StateView";
import { Progress } from "@/components/ui/Status";
import { problemMessages } from "@/lib/notifications/types";
import { engagementApi, send, type CommunityView, type PollView } from "@/lib/engagement";
import "./engagement.css";

/** A `datetime-local` value a week from now, in the browser's time. */
function inAWeek(): string {
  const d = new Date(Date.now() + 7 * 86_400_000);
  d.setMinutes(d.getMinutes() - d.getTimezoneOffset(), 0, 0);
  return d.toISOString().slice(0, 16);
}

/**
 * Polls in a community (spec 023 US3 scenario 1). Members who can post start a poll; everyone who reads the community
 * votes. What each person may do and whether results show come from the API.
 */
export function CommunityPolls({ community, initial }: { community: CommunityView; initial: PollView[] }) {
  const t = useTranslations("engagement.polls");
  const [polls, setPolls] = useState(initial);
  const replace = (view: PollView) => setPolls((all) => all.map((p) => (p.id === view.id ? view : p)));

  return (
    <div className="engagement">
      {community.rights.canPost ? <CreatePoll communityId={community.id} onCreated={(view) => setPolls((all) => [view, ...all])} /> : null}
      {polls.length === 0 ? (
        <StateView kind="empty" title={t("emptyTitle")} message={community.rights.canPost ? t("emptyPoster") : t("empty")} icon="chat" />
      ) : (
        <ul className="engagement-feed" aria-label={t("listLabel")}>
          {polls.map((p) => (
            <li key={p.id}>
              <PollCard poll={p} onChange={replace} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function CreatePoll({ communityId, onCreated }: { communityId: string; onCreated: (view: PollView) => void }) {
  const t = useTranslations("engagement.polls");
  const [question, setQuestion] = useState("");
  const [options, setOptions] = useState("");
  const [multiple, setMultiple] = useState(false);
  const [anonymous, setAnonymous] = useState(false);
  const [allowChange, setAllowChange] = useState(false);
  const [results, setResults] = useState<"after_vote" | "after_close">("after_vote");
  const [closesAt, setClosesAt] = useState(inAWeek);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [created, setCreated] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setCreated(false);
    try {
      const response = await send(`${engagementApi}/communities/${communityId}/polls`, "POST", {
        question,
        options: options.split("\n").filter((o) => o.trim()),
        multiple,
        anonymous,
        allowChange: allowChange && !anonymous,
        results,
        closesAt: closesAt ? new Date(closesAt).toISOString() : null,
      });
      if (response.ok) {
        onCreated((await response.json()) as PollView);
        setQuestion("");
        setOptions("");
        setCreated(true);
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
    <section className="engagement-section" aria-labelledby="engagement-new-poll">
      <h2 id="engagement-new-poll">{t("createTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        <FormField label={t("question")} name="poll-question" value={question} maxLength={300} dir="auto" onChange={(e) => setQuestion(e.target.value)} />
        <TextAreaField label={t("options")} name="poll-options" hint={t("optionsHint")} value={options} rows={4} dir="auto" onChange={(e) => setOptions(e.target.value)} />
        <FormField label={t("closesAt")} name="poll-closes" type="datetime-local" value={closesAt} onChange={(e) => setClosesAt(e.target.value)} />
        <RadioGroup
          legend={t("results")}
          name="poll-results"
          value={results}
          onChange={(e) => setResults(e.target.value as "after_vote" | "after_close")}
          options={[
            { value: "after_vote", label: t("resultsAfterVote") },
            { value: "after_close", label: t("resultsAfterClose") },
          ]}
        />
        <Checkbox label={t("multiple")} checked={multiple} onChange={(e) => setMultiple(e.target.checked)} />
        <Checkbox label={t("anonymous")} hint={t("anonymousHint")} checked={anonymous} onChange={(e) => setAnonymous(e.target.checked)} />
        {anonymous ? null : <Checkbox label={t("allowChange")} checked={allowChange} onChange={(e) => setAllowChange(e.target.checked)} />}
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {created ? <Alert tone="success" title={t("created")} live /> : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" icon="plus" busy={busy} busyLabel={t("working")}>
            {t("create")}
          </Button>
        </div>
      </form>
    </section>
  );
}

function PollCard({ poll, onChange }: { poll: PollView; onChange: (view: PollView) => void }) {
  const t = useTranslations("engagement.polls");
  const format = useFormatter();
  const [choice, setChoice] = useState<string[]>(poll.myChoices);
  const [adding, setAdding] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const headingId = `poll-${poll.id}`;
  const total = poll.options.reduce((sum, o) => sum + (o.count ?? 0), 0);

  async function act(kind: string, call: () => Promise<Response>) {
    setBusy(kind);
    setMessages([]);
    try {
      const response = await call();
      if (response.ok) {
        const view = (await response.json()) as PollView;
        onChange(view);
        setChoice(view.myChoices);
        if (kind === "add") setAdding("");
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  const when = format.dateTime(new Date(poll.closesAt), { dateStyle: "medium", timeStyle: "short" });
  return (
    <article className="engagement-post" aria-labelledby={headingId}>
      <div className="engagement-post-head">
        <h3 id={headingId} className="engagement-post-author" dir="auto">
          {poll.question}
        </h3>
        <Badge tone={poll.closed ? "neutral" : "success"}>{poll.closed ? t("closed") : t("open")}</Badge>
        {poll.anonymous ? <Badge tone="accent">{t("anonymousBadge")}</Badge> : null}
        <p className="engagement-meta">
          <bdi>{poll.author.displayName}</bdi>
          {" · "}
          {poll.closed ? t("closedOn", { when }) : t("closesOn", { when })}
          {poll.voters !== null ? ` · ${t("voters", { count: poll.voters })}` : null}
        </p>
      </div>
      {poll.canVote ? (
        <form
          className="engagement"
          onSubmit={(e) => {
            e.preventDefault();
            void act("vote", () => send(`${engagementApi}/polls/${poll.id}/vote`, "PUT", { optionIds: choice }));
          }}
        >
          {poll.multiple ? (
            <fieldset className="ui-choice-group">
              <legend>{t("chooseMany")}</legend>
              {poll.options.map((o) => (
                <Checkbox
                  key={o.id}
                  label={o.label}
                  checked={choice.includes(o.id)}
                  onChange={(e) => setChoice((all) => (e.target.checked ? [...all, o.id] : all.filter((x) => x !== o.id)))}
                />
              ))}
            </fieldset>
          ) : (
            <RadioGroup legend={t("chooseOne")} name={`poll-${poll.id}`} value={choice[0] ?? ""} onChange={(e) => setChoice([e.target.value])} options={poll.options.map((o) => ({ value: o.id, label: o.label }))} />
          )}
          {poll.anonymous ? <p className="engagement-meta">{t("anonymousVote")}</p> : null}
          <div className="engagement-row">
            <Button type="submit" variant="primary" size="sm" disabled={choice.length === 0} busy={busy === "vote"} busyLabel={t("working")}>
              {poll.voted ? t("changeVote") : t("vote")}
            </Button>
          </div>
        </form>
      ) : null}
      {poll.resultsVisible ? (
        <div className="engagement" role="group" aria-label={t("resultsLabel")}>
          {poll.options.map((o) => (
            <div key={o.id}>
              <Progress label={o.label} value={o.count ?? 0} max={Math.max(total, 1)} valueText={t("votes", { count: o.count ?? 0 })} />
              {o.voters && o.voters.length > 0 ? (
                <p className="engagement-meta">
                  {o.voters.map((v, i) => (
                    <span key={v.id}>
                      {i > 0 ? ", " : null}
                      <bdi>{v.displayName}</bdi>
                    </span>
                  ))}
                </p>
              ) : null}
            </div>
          ))}
        </div>
      ) : (
        <p className="engagement-meta">{poll.voted ? t("resultsAtClose") : poll.results === "after_close" ? t("resultsAtClose") : t("resultsAfterVoting")}</p>
      )}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {poll.canAddOptions || poll.canClose ? (
        <div className="engagement-row">
          {poll.canAddOptions ? (
            <>
              <FormField label={t("addOption")} name={`add-${poll.id}`} value={adding} maxLength={120} dir="auto" onChange={(e) => setAdding(e.target.value)} />
              <Button size="sm" icon="plus" disabled={!adding.trim()} busy={busy === "add"} busyLabel={t("working")}
                onClick={() => void act("add", () => send(`${engagementApi}/polls/${poll.id}/options`, "POST", { options: [adding] }))}>
                {t("add")}
              </Button>
            </>
          ) : null}
          {poll.canClose ? (
            <Button size="sm" variant="quiet" busy={busy === "close"} busyLabel={t("working")} onClick={() => void act("close", () => send(`${engagementApi}/polls/${poll.id}:close`, "POST"))}>
              {t("close")}
            </Button>
          ) : null}
        </div>
      ) : null}
    </article>
  );
}
