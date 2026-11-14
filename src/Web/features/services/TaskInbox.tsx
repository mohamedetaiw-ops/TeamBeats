"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMemo, useRef, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { useNow } from "@/lib/clock";
import { Icon, type IconName } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { Button, ButtonLink } from "@/components/ui/Button";
import { pick, type InboxItem } from "@/lib/services/types";
import { isolate } from "@/lib/bidi";
import type { OrgUnit } from "@/lib/experience/builder-types";
import { TaskReviewDrawer } from "./TaskReviewDrawer";
import "./services.css";

// The icon of each kind of work, so a queue can be scanned by type (spec 036 DLS-108).
const sourceIcons: Record<string, IconName> = {
  content_review: "news",
  content_owner_review: "news",
  content_retire: "news",
  policy_approval: "policy",
  policy_attestation: "policy",
  agent_review: "spark",
  agent_prepared: "spark",
  eval_follow_up: "spark",
  external_action: "spark",
  leave: "leave",
  leave_change: "leave",
  leave_replacement: "leave",
  journey: "user",
  attendance: "clock",
  letter: "files",
  data_change: "edit",
  expense: "files",
  booking: "building",
  asset_access: "image",
};
const dayMs = 86_400_000;

/**
 * The task inbox (spec 016 US3, FR-007, FR-020): everything waiting for the signed-in person, from form services and
 * other sources, with type, requester, age and due date, filterable by type and with late work first. Every row shows
 * its next action: Review opens a request in a panel beside the queue, where its details and the decision are; Open
 * goes to the page the task's source names. Nothing is approved from the list itself.
 */
export function TaskInbox({ items, meId, units }: { items: InboxItem[]; meId: string; units: OrgUnit[] }) {
  const t = useTranslations("services.inbox");
  const locale = useLocale();
  const format = useFormatter();
  const now = useNow();
  const [type, setType] = useState("");
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const [review, setReview] = useState<{ taskId: string; requestId: string; title: string } | null>(null);
  const [decided, setDecided] = useState<{ ids: string[]; last: string | null }>({ ids: [], last: null });
  const list = useRef<HTMLUListElement>(null);

  // After a decision the panel closes, the row leaves, a message says what happened and focus moves to the next row.
  function onDecided(taskId: string, outcome: string) {
    const rows = [...(list.current?.querySelectorAll<HTMLElement>("[data-task]") ?? [])];
    const at = rows.findIndex((row) => row.dataset.task === taskId);
    const next = rows[at + 1] ?? rows[at - 1];
    setReview(null);
    setDecided((d) => ({ ids: [...d.ids, taskId], last: outcome }));
    window.setTimeout(() => next?.querySelector<HTMLElement>("[data-row-action]")?.focus(), 0);
    router.refresh();
  }

  // Pick a queue task, or put it back for the rest of the queue (spec 016 US6 scenario 1).
  async function pickTask(taskId: string, pick: boolean) {
    setBusy(taskId);
    setFailed(false);
    try {
      const response = await fetch(`/api/v1/tasks/${taskId}/pick`, { method: pick ? "POST" : "DELETE" });
      if (response.ok) {
        router.refresh();
      } else {
        setFailed(true);
      }
    } catch {
      setFailed(true);
    } finally {
      setBusy(null);
    }
  }

  const types = useMemo(() => {
    const seen = new Map<string, string>();
    for (const i of items) {
      const key = i.serviceId ?? i.source;
      if (!seen.has(key)) seen.set(key, i.serviceNames ? pick(i.serviceNames, locale) : sourceName(i));
    }
    return [...seen.entries()];
  }, [items, locale]);
  function sourceName(i: InboxItem) {
    switch (i.source) {
      case "content_review":
        return t("contentReview");
      case "content_owner_review":
        return t("contentOwnerReview");
      case "content_retire":
        return t("contentRetire");
      case "policy_approval":
        return t("policyApproval");
      case "policy_attestation":
        return t("policyAttestation");
      case "agent_review":
        return t("agentReview");
      case "agent_prepared":
        return t("preparedByAi");
      case "eval_follow_up":
        return t("evalFollowUp");
      case "leave":
        return t("leaveApproval");
      case "leave_change":
        return t("leaveChange");
      case "leave_replacement":
        return t("leaveReplacement");
      case "journey":
        return t("journeyStep");
      case "attendance":
        return t("attendanceRequest");
      case "letter":
        return t("letterRequest");
      case "data_change":
        return t("dataChangeRequest");
      case "expense":
        return t("expenseClaim");
      case "booking":
        return t("bookingRequest");
      case "external_action":
        return t("aiAction");
      case "asset_access":
        return t("assetAccess");
      default:
        return i.name;
    }
  }
  const late = (i: InboxItem) => i.dueAt !== null && now !== null && new Date(i.dueAt).getTime() < Number(now);
  const shown = items
    .filter((i) => !decided.ids.includes(i.taskId) && (!type || (i.serviceId ?? i.source) === type))
    .map((i, at) => ({ i, at }))
    // Late work first, then what is due soonest; tasks with no due date keep the order the API gave.
    .sort((a, b) => Number(late(b.i)) - Number(late(a.i)) || (a.i.dueAt && b.i.dueAt ? a.i.dueAt.localeCompare(b.i.dueAt) : Number(!a.i.dueAt) - Number(!b.i.dueAt)) || a.at - b.at)
    .map((x) => x.i);

  return (
    <section className="services-block" aria-labelledby="inbox-title">
      <div className="services-block-head">
        <h2 id="inbox-title">{t("title", { count: items.length - decided.ids.length })}</h2>
      {types.length > 1 ? (
        <label className="services-select services-inline-select">
          <span>{t("type")}</span>
          <select value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">{t("allTypes")}</option>
            {types.map(([key, name]) => (
              <option key={key} value={key}>
                {name}
              </option>
            ))}
          </select>
        </label>
      ) : null}
      </div>
      {failed ? (
        <div className="notice notice-error" role="alert">
          <p>{t("pickFailed")}</p>
        </div>
      ) : null}
      <div aria-live="polite">
        {decided.last ? (
          <p className="notice" role="status" data-testid="task-decided">
            {t(`decided.${decided.last === "approve" || decided.last === "reject" ? decided.last : "return"}`)}
          </p>
        ) : null}
      </div>
      {shown.length === 0 ? (
        <StateView kind="empty" title={t("empty")} message="" />
      ) : (
        <ul className="ui-rows" data-testid="task-inbox" ref={list}>
          {shown.map((i) => {
            const title = i.serviceNames ? pick(i.serviceNames, locale) : i.title ? pick(i.title, locale) : sourceName(i);
            return (
              <li key={i.taskId} className="ui-row" data-task={i.taskId}>
                <span className="ui-icon-tile" data-tone={late(i) ? "highlight" : "accent"}>
                  <Icon name={i.requestId ? "services" : (sourceIcons[i.source] ?? "tasks")} size={20} />
                </span>
                <div className="ui-row-text">
                {i.requestId ? (
                  <Link className="ui-row-link" href={`/requests/${i.requestId}?task=${i.taskId}`}>
                    <bdi>{title}</bdi>
                    {i.number ? (
                      <>
                        {" · "}
                        <bdi dir="ltr">{i.number}</bdi>
                      </>
                    ) : null}
                  </Link>
                ) : i.link && /^\/(?!\/)/.test(i.link) ? (
                  <Link className="ui-row-link" href={i.link}>
                    <bdi>{title}</bdi>
                  </Link>
                ) : (
                  <bdi>{title}</bdi>
                )}
                <span className="services-meta">
                  {i.step ? <bdi>{pick(i.step, locale)}</bdi> : i.title ? <bdi>{sourceName(i)}</bdi> : null}
                  {i.requester ? <> · {t("from", { name: isolate(i.requester.displayName) })}</> : null}
                  {i.requestedFor ? <> · {t("for", { name: isolate(i.requestedFor.displayName) })}</> : null}
                </span>
                {now === null ? null : (
                  // Age and due date wait for the browser: the server has neither the viewer's clock nor time zone.
                  <span className="services-meta">
                    {t("age", { when: format.relativeTime(new Date(i.createdAt), now) })}
                    {i.dueAt ? (
                      <>
                        {" · "}
                        {late(i) ? (
                          <strong className="services-late">
                            {t("dueLate", {
                              when: format.dateTime(new Date(i.dueAt), { dateStyle: "medium" }),
                              days: Math.max(1, Math.floor((Number(now) - new Date(i.dueAt).getTime()) / dayMs)),
                            })}
                          </strong>
                        ) : (
                          t("due", { when: format.dateTime(new Date(i.dueAt), { dateStyle: "medium" }) })
                        )}
                      </>
                    ) : null}
                  </span>
                )}
                </div>
                {i.onBehalfOf ? <span className="ui-badge">{t("onBehalfOf", { name: isolate(i.onBehalfOf.displayName) })}</span> : null}
                {i.pickable ? (
                  i.pickedBy ? (
                    <>
                      <span className="ui-badge">{t("pickedBy", { name: isolate(i.pickedBy.displayName) })}</span>
                      <Button variant="secondary" disabled={busy === i.taskId} onClick={() => pickTask(i.taskId, false)}>
                        {t("putBack")}
                      </Button>
                    </>
                  ) : (
                    <Button variant="secondary" disabled={busy === i.taskId} onClick={() => pickTask(i.taskId, true)}>
                      {t("pick")}
                    </Button>
                  )
                ) : null}
                {i.requestId ? (
                  <Button variant="secondary" size="sm" data-row-action onClick={() => setReview({ taskId: i.taskId, requestId: i.requestId!, title: i.number ? `${title} · ${i.number}` : title })}>
                    {t("review")}
                  </Button>
                ) : i.link && /^\/(?!\/)/.test(i.link) ? (
                  <ButtonLink variant="secondary" size="sm" data-row-action href={i.link}>
                    {t("open")}
                  </ButtonLink>
                ) : null}
              </li>
            );
          })}
        </ul>
      )}
      <TaskReviewDrawer task={review} meId={meId} units={units} onClose={() => setReview(null)} onDecided={onDecided} />
    </section>
  );
}
