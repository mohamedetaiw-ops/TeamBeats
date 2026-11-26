"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { SelectField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { Widget } from "@/components/ui/Widget";
import { call, send, spacesApi, type SpaceSync, type SyncEvent, type TrackerContainer } from "@/lib/spaces";
import "./spaces.css";

type Tone = "neutral" | "success" | "warning" | "danger" | "info";

const eventTone: Record<SyncEvent["kind"], Tone> = { run: "neutral", conflict: "warning", failed: "danger", skipped_assignee: "info", unlinked_task: "info" };

/**
 * Task list sync with Planner or Jira for the space's owners (spec 029 R3 US10): the links with their last run, the form
 * that links a list to a plan or project, and the history of runs, conflicts and failures. The API decides who may do
 * this and what is synced; this part shows what it answers.
 */
export function SyncPanel({ spaceId, initial, lists }: { spaceId: string; initial: SpaceSync; lists: { id: string; name: string }[] }) {
  const t = useTranslations("spaces.sync");
  const format = useFormatter();
  const base = `${spacesApi}/${spaceId}/sync`;
  const [view, setView] = useState(initial);
  const [listId, setListId] = useState("");
  const [connectionId, setConnectionId] = useState("");
  const [containers, setContainers] = useState<TrackerContainer[] | null>(null);
  const [containerId, setContainerId] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [done, setDone] = useState<string | null>(null);

  async function act(key: string, request: () => Promise<Response>, success: string) {
    setBusy(key);
    setDone(null);
    const result = await call<SpaceSync>(request, t("failed"));
    setBusy(null);
    if (!result.ok) {
      setMessages(result.messages);
      return false;
    }
    setMessages([]);
    setView(result.value);
    setDone(success);
    return true;
  }

  async function choose(connection: string) {
    setConnectionId(connection);
    setContainerId("");
    setContainers(null);
    if (!connection) return;
    const result = await call<{ result: string; items: TrackerContainer[] }>(() => fetch(`${base}/containers?connection=${encodeURIComponent(connection)}`), t("failed"));
    if (!result.ok) {
      setMessages(result.messages);
      return;
    }
    if (result.value.result !== "ok") {
      setMessages([t("trackerDown")]);
      return;
    }
    setMessages([]);
    setContainers(result.value.items);
  }

  const when = (at: string) => format.dateTime(new Date(at), { dateStyle: "medium", timeStyle: "short" });
  const linkedLists = new Set(view.links.map((l) => l.listId));
  const free = lists.filter((l) => !linkedLists.has(l.id));

  if (!view.available) {
    return <StateView kind="unsupported" title={t("notInPackageTitle")} message={t("notInPackage")} />;
  }

  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {done ? <Alert tone="success" title={done} live /> : null}

      <div className="spaces-split">
        <Widget title={t("linksTitle")}>
          {view.links.length === 0 ? (
            <StateView kind="empty" title={t("noLinksTitle")} message={t("noLinks")} />
          ) : (
            <ul className="spaces-list" aria-label={t("linksTitle")}>
              {view.links.map((link) => (
                <li key={link.id} className="spaces-row" data-testid="space-sync-link">
                  <span>
                    {t("link", { list: link.listName, container: link.containerName })}
                  </span>
                  <Badge tone="info">{t(`kinds.${link.kind}`)}</Badge>
                  <span className="spaces-meta">
                    {t("linkMeta", { tasks: link.tasks, last: link.lastRunAt ? when(link.lastRunAt) : t("never") })}
                  </span>
                  <Button size="sm" busy={busy === `run-${link.id}`} busyLabel={t("working")}
                    onClick={() => void act(`run-${link.id}`, () => send(`${base}/links/${link.id}:run`, "POST"), t("ran"))}>
                    {t("runNow")}
                  </Button>
                  <Button size="sm" variant="quiet" icon="close" busy={busy === `unlink-${link.id}`} busyLabel={t("working")}
                    onClick={() => void act(`unlink-${link.id}`, () => send(`${base}/links/${link.id}`, "DELETE"), t("unlinked"))}>
                    {t("unlink", { list: link.listName })}
                  </Button>
                </li>
              ))}
            </ul>
          )}
        </Widget>

        <Widget title={t("linkTitle")}>
          {view.connections.length === 0 ? (
            <StateView kind="empty" title={t("noConnectionsTitle")} message={t("noConnections")} />
          ) : free.length === 0 ? (
            <p className="spaces-meta">{t("allLinked")}</p>
          ) : (
            <form
              className="spaces-form"
              aria-label={t("linkTitle")}
              onSubmit={(e) => {
                e.preventDefault();
                void act("link", () => send(`${base}/links`, "POST", { listId, connectionId, containerId }), t("linked")).then((ok) => {
                  if (ok) {
                    setListId("");
                    setContainerId("");
                  }
                });
              }}
            >
              <p className="spaces-meta">{t("linkIntro")}</p>
              <SelectField label={t("list")} name="listId" value={listId} onChange={(e) => setListId(e.target.value)} required
                options={[{ value: "", label: t("chooseList") }, ...free.map((l) => ({ value: l.id, label: l.name }))]} />
              <SelectField label={t("connection")} name="connectionId" value={connectionId} onChange={(e) => void choose(e.target.value)} required
                options={[{ value: "", label: t("chooseConnection") }, ...view.connections.map((c) => ({ value: c.id, label: `${c.displayName} (${t(`kinds.${c.kind}`)})` }))]} />
              {containers ? (
                <SelectField label={t("container")} name="containerId" value={containerId} onChange={(e) => setContainerId(e.target.value)} required
                  options={[{ value: "", label: t("chooseContainer") }, ...containers.map((c) => ({ value: c.id, label: c.name }))]} />
              ) : null}
              <div className="spaces-row">
                <Button type="submit" variant="primary" icon="link" busy={busy === "link"} busyLabel={t("working")} disabled={!containers}>
                  {t("linkSubmit")}
                </Button>
              </div>
            </form>
          )}
        </Widget>
      </div>

      <Widget title={t("historyTitle")}>
        {view.history.length === 0 ? (
          <p className="spaces-meta">{t("noHistory")}</p>
        ) : (
          <ul className="spaces-list" aria-label={t("historyTitle")}>
            {view.history.map((e) => (
              <li key={e.id} className="spaces-row">
                <Badge tone={e.kind === "run" && e.outcome && e.outcome !== "ok" ? "danger" : eventTone[e.kind]}>{t(`event.${e.kind}`)}</Badge>
                <span>
                  {e.kind === "run"
                    ? t("runLine", { pulled: e.pulled, pushed: e.pushed, outcome: t(`outcome.${e.outcome ?? "ok"}`) })
                    : e.kind === "conflict"
                      ? t("conflictLine", { title: e.taskTitle ?? "", winner: t(`outcome.${e.outcome ?? "space_won"}`) })
                      : e.kind === "failed"
                        ? t("failedLine", { title: e.taskTitle ?? "", outcome: t(`outcome.${e.outcome ?? "unavailable"}`) })
                        : e.kind === "skipped_assignee"
                          ? t("skippedLine", { title: e.taskTitle ?? "", person: e.detail ?? "" })
                          : t("unlinkedLine", { title: e.taskTitle ?? "" })}
                </span>
                <span className="spaces-meta">{when(e.at)}</span>
              </li>
            ))}
          </ul>
        )}
      </Widget>
    </div>
  );
}
