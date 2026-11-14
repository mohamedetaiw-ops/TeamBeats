"use client";

import Link from "next/link";
import { useEffect, useId, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import { readHref, type CriticalNotice, type PendingCritical } from "@/lib/content/types";
import { BodyView } from "./BodyView";
import "./content.css";

type Action = "acknowledge" | "remind-later" | "dismiss";

/**
 * Critical announcements in the shell (spec 009 US2, scenario 6): a pop-up the person answers on their next visit and
 * banners above the content of every page, in their language. The API decides who is shown what and records each
 * answer with the version and channel; when it cannot be reached nothing is shown, so the page is never blocked.
 */
export function CriticalNotices() {
  const t = useTranslations("content.critical");
  const locale = useLocale();
  const ids = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const [pending, setPending] = useState<PendingCritical>({ modal: null, banners: [] });
  const [attempt, setAttempt] = useState(0);
  const [busy, setBusy] = useState<string | null>(null);
  const [errors, setErrors] = useState<string[]>([]);

  useEffect(() => {
    let live = true;
    fetch("/api/v1/content/critical/pending", { cache: "no-store" })
      .then(async (r) => {
        if (r.ok && live) setPending((await r.json()) as PendingCritical);
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, [attempt]);

  const modal = pending.modal;
  useEffect(() => {
    const el = dialog.current;
    if (!el) return;
    if (modal && !el.open) el.showModal();
    if (!modal && el.open) el.close();
  }, [modal]);

  async function act(notice: CriticalNotice, action: Action) {
    setBusy(`${notice.id}:${action}`);
    setErrors([]);
    const response = await fetch(`/api/v1/content/items/${encodeURIComponent(notice.id)}/${action}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: action === "acknowledge" ? JSON.stringify({ locale: notice.locale, channel: "web" }) : undefined,
    }).catch(() => null);
    setBusy(null);
    if (response?.ok) {
      setAttempt((a) => a + 1);
      return;
    }
    const messages = response ? await problemMessages(response) : [];
    setErrors(messages.length > 0 ? messages : [t("failed")]);
  }

  const deadline = (iso: string | null) => (iso ? new Intl.DateTimeFormat(locale, { dateStyle: "long", timeStyle: "short" }).format(new Date(iso)) : null);

  return (
    <>
      {pending.banners.length > 0 ? (
        <div className="content-banners shell-critical" data-shell-critical="">
          {pending.banners.map((notice) => (
            <section key={notice.id} className="content-banner" aria-label={notice.title} lang={notice.locale}>
              <p className="content-banner-title">
                <span className="ui-badge content-badge" data-tone="accent">{t("label")}</span>
                <Link href={readHref(notice.id)}>
                  <bdi>{notice.title}</bdi>
                </Link>
              </p>
              {notice.summary ? (
                <p className="content-meta">
                  <bdi>{notice.summary}</bdi>
                </p>
              ) : null}
              {notice.requireAck || notice.dismissible ? (
                <div className="content-actions">
                  {notice.requireAck ? (
                    <Button variant="primary" busy={busy === `${notice.id}:acknowledge`} busyLabel={t("saving")} onClick={() => void act(notice, "acknowledge")}>
                      {t("confirm")}
                    </Button>
                  ) : null}
                  {notice.dismissible ? (
                    <Button variant="quiet" busy={busy === `${notice.id}:dismiss`} busyLabel={t("saving")} onClick={() => void act(notice, "dismiss")}>
                      {t("dismiss")}
                    </Button>
                  ) : null}
                </div>
              ) : null}
            </section>
          ))}
          {errors.length > 0 && !modal ? (
            <p className="field-error" role="alert">
              {errors.join(" ")}
            </p>
          ) : null}
        </div>
      ) : null}
      <dialog
        ref={dialog}
        className="content-dialog"
        aria-labelledby={`${ids}-title`}
        aria-describedby={`${ids}-summary`}
        lang={modal?.locale}
        onCancel={(e) => {
          // A pop-up always needs an answer (confirm or remind me later), so Escape does not close it.
          e.preventDefault();
        }}
      >
        {modal ? (
          <div className="content-dialog-body">
            <p className="ui-badge content-badge" data-tone="accent">{t("label")}</p>
            <h2 id={`${ids}-title`}>
              <bdi>{modal.title}</bdi>
            </h2>
            {modal.summary ? (
              <p id={`${ids}-summary`} className="content-summary">
                <bdi>{modal.summary}</bdi>
              </p>
            ) : null}
            <div className="content-dialog-scroll">
              <BodyView body={modal.body} embedTitle={t("embed")} />
            </div>
            {modal.deadlineAt ? <p className="content-meta">{t("deadline", { date: deadline(modal.deadlineAt) ?? "" })}</p> : null}
            {errors.length > 0 ? (
              <p className="field-error" role="alert">
                {errors.join(" ")}
              </p>
            ) : null}
            <div className="content-actions">
              <Button variant="primary" busy={busy === `${modal.id}:acknowledge`} busyLabel={t("saving")} onClick={() => void act(modal, "acknowledge")}>
                {t("confirm")}
              </Button>
              {modal.remindLaterLeft > 0 ? (
                <Button busy={busy === `${modal.id}:remind-later`} busyLabel={t("saving")} onClick={() => void act(modal, "remind-later")}>
                  {t("remindLater", { count: modal.remindLaterLeft })}
                </Button>
              ) : null}
            </div>
          </div>
        ) : null}
      </dialog>
    </>
  );
}
