"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useMemo, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { useNow } from "@/lib/clock";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { Button } from "@/components/ui/Button";
import { pick, type InboxItem } from "@/lib/services/types";
import { isolate } from "@/lib/bidi";
import "./services.css";

/**
 * The task inbox (spec 016 US3, FR-007): everything waiting for the signed-in person, from form services and other
 * sources, with type, requester, age and due date, filterable by type. Each task opens its request, or the page its source names, to decide.
 */
export function TaskInbox({ items }: { items: InboxItem[] }) {
  const t = useTranslations("services.inbox");
  const locale = useLocale();
  const format = useFormatter();
  const now = useNow();
  const [type, setType] = useState("");
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

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
  const shown = items.filter((i) => !type || (i.serviceId ?? i.source) === type);

  return (
    <section className="services-block" aria-labelledby="inbox-title">
      <div className="services-block-head">
        <h2 id="inbox-title">{t("title", { count: items.length })}</h2>
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
      {shown.length === 0 ? (
        <StateView kind="empty" title={t("empty")} message="" />
      ) : (
        <ul className="ui-rows" data-testid="task-inbox">
          {shown.map((i) => {
            const title = i.serviceNames ? pick(i.serviceNames, locale) : i.title ? pick(i.title, locale) : sourceName(i);
            return (
              <li key={i.taskId} className="ui-row">
                <span className="ui-icon-tile" data-tone={i.dueAt && now !== null && new Date(i.dueAt).getTime() < Number(now) ? "highlight" : "accent"}>
                  <Icon name={i.requestId ? "inbox" : "tasks"} size={20} />
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
                    {i.dueAt ? <> · {t("due", { when: format.dateTime(new Date(i.dueAt), { dateStyle: "medium" }) })}</> : null}
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
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
