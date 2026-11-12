"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { call, send, spacesApi, type SpaceGuests } from "@/lib/spaces";
import "./spaces.css";

/**
 * Guests of a space for its owners (spec 029 US4, FR-007, FR-008). Owners invite partners by email as viewers or
 * members, renew their access, cancel open invitations and decide whether guests open Confidential files. What is
 * allowed, and for how long, comes from the server; inviting and renewing may ask for a fresh sign-in.
 */
export function Guests({ spaceId, initial }: { spaceId: string; initial: SpaceGuests }) {
  const t = useTranslations("spaces.guests");
  const format = useFormatter();
  const router = useRouter();
  const [view, setView] = useState(initial);
  const [email, setEmail] = useState("");
  const [role, setRole] = useState("viewer");
  const [days, setDays] = useState(String(initial.maxDays));
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const url = `${spacesApi}/${spaceId}/guests`;
  const date = (instant: string | null) => (instant ? format.dateTime(new Date(instant), { dateStyle: "medium" }) : "");

  async function apply(run: () => Promise<Response>, done: string) {
    setNotice(null);
    const result = await call<SpaceGuests>(run, t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setView(result.value);
    setNotice(done);
    router.refresh();
  }

  async function invite() {
    await apply(() => send(url, "POST", { email: email.trim(), role, days: Number(days) }), t("invited", { email: email.trim() }));
    setEmail("");
  }

  if (!view.allowed) {
    return (
      <section className="spaces-section" aria-labelledby="guests-title">
        <h2 id="guests-title">{t("title")}</h2>
        <p className="spaces-meta">{t("off")}</p>
      </section>
    );
  }

  return (
    <section className="spaces-section" aria-labelledby="guests-title">
      <h2 id="guests-title">{t("title")}</h2>
      <p className="spaces-meta">{t("intro", { days: view.maxDays, limit: view.limit })}</p>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {notice ? (
        <p role="status" className="spaces-meta">
          {notice}
        </p>
      ) : null}

      {view.guests.length === 0 ? <p className="spaces-meta">{t("none")}</p> : null}
      <ul className="spaces-list" aria-label={t("listLabel")}>
        {view.guests.map((g) => (
          <li key={g.userId} className="spaces-member">
            <bdi>{g.displayName}</bdi>
            <Badge tone="warning">{t("guest")}</Badge>
            <span className="spaces-meta">{t("until", { date: date(g.expiresAt) })}</span>
            <Button size="sm" variant="quiet" onClick={() => void apply(() => send(`${url}/${g.userId}`, "PUT", { days: view.maxDays }), t("renewed", { name: g.displayName }))}>
              {t("renew")}
            </Button>
          </li>
        ))}
      </ul>

      {view.invitations.length > 0 ? (
        <>
          <h3>{t("invitationsTitle")}</h3>
          <ul className="spaces-list" aria-label={t("invitationsTitle")}>
            {view.invitations.map((i) => (
              <li key={i.id} className="spaces-member">
                <bdi>{i.email}</bdi>
                <Badge tone={i.emailState === "sent" ? "neutral" : "danger"}>{t(`email.${i.emailState}`)}</Badge>
                <span className="spaces-meta">{t("until", { date: date(i.expiresAt) })}</span>
                <Button size="sm" variant="quiet" onClick={() => void apply(() => send(`${url}/invitations/${i.id}`, "DELETE"), t("cancelled", { email: i.email }))}>
                  {t("cancel")}
                </Button>
              </li>
            ))}
          </ul>
        </>
      ) : null}

      {view.canInvite ? (
        <form
          className="spaces-form"
          onSubmit={(e) => {
            e.preventDefault();
            void invite();
          }}
        >
          <h3>{t("inviteTitle")}</h3>
          <FormField label={t("emailLabel")} name="guestEmail" type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={254} required />
          <div className="spaces-row">
            <SelectField label={t("role")} name="guestRole" value={role} onChange={(e) => setRole(e.target.value)}
              options={[{ value: "viewer", label: t("roleViewer") }, { value: "member", label: t("roleMember") }]} />
            <FormField label={t("days")} name="guestDays" type="number" min={1} max={view.maxDays} value={days} onChange={(e) => setDays(e.target.value)} required />
          </div>
          <div className="spaces-row">
            <Button type="submit" icon="plus">
              {t("invite")}
            </Button>
          </div>
        </form>
      ) : null}

      <Checkbox label={t("confidential")} hint={view.confidentialAllowed ? t("confidentialHint") : t("confidentialOff")} checked={view.confidential}
        disabled={!view.confidentialAllowed}
        onChange={(e) => void apply(() => send(`${url}/files`, "PUT", { confidential: e.target.checked }), t("saved"))} />
    </section>
  );
}
