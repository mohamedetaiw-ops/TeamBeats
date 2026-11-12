"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { SelectField } from "@/components/ui/FormField";
import { PersonPicker } from "@/features/services/PersonPicker";
import { problemMessages } from "@/lib/notifications/types";
import { engagementApi, send, type CommunityView, type MemberRole, type MemberView } from "@/lib/engagement";
import "./engagement.css";

const roles: MemberRole[] = ["member", "moderator", "owner"];

/**
 * Community members (spec 023 US1, section 3). Everyone who reads the community sees who is in it; owners also see
 * requests to join, add people, change roles and remove members. People who come from the linked org unit follow the
 * unit, so they are listed but not removed here. The API checks every change again.
 */
export function CommunityMembers({ community, initial }: { community: CommunityView; initial: MemberView[] }) {
  const t = useTranslations("engagement");
  const [members, setMembers] = useState(initial);
  const [adding, setAdding] = useState<string | null>(null);
  const [role, setRole] = useState<MemberRole>("member");
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const manage = community.rights.canManage;
  const base = `${engagementApi}/communities/${community.id}/members`;

  async function change(call: Promise<Response>, done: string) {
    setMessages([]);
    setNotice(null);
    try {
      const response = await call;
      if (response.ok) {
        setMembers((await response.json()) as MemberView[]);
        setNotice(done);
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    }
  }

  return (
    <div className="engagement">
      {notice ? <Alert tone="success" title={notice} live /> : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <section className="engagement-section" aria-labelledby="engagement-members">
        <h2 id="engagement-members">{t("members.title", { count: members.filter((m) => m.status === "active").length })}</h2>
        <ul className="engagement-members">
          {members.map((m) => (
            <li key={m.userId}>
              <span>
                <bdi>{m.displayName}</bdi>
              </span>
              <Badge tone={m.role === "owner" ? "accent" : m.role === "moderator" ? "info" : "neutral"}>{t(`roles.${m.role}`)}</Badge>
              {m.status === "pending" ? <Badge tone="warning">{t("members.pending")}</Badge> : null}
              {m.source === "org_unit" ? <Badge tone="neutral">{t("members.fromUnit")}</Badge> : null}
              {manage && m.status === "pending" ? (
                <>
                  <Button size="sm" variant="primary" onClick={() => void change(send(base, "PUT", { userId: m.userId, role: "member" }), t("members.accepted", { name: m.displayName }))}>
                    {t("members.accept")}
                  </Button>
                  <Button size="sm" variant="quiet" onClick={() => void change(send(`${base}/${m.userId}`, "DELETE"), t("members.declined", { name: m.displayName }))}>
                    {t("members.decline")}
                  </Button>
                </>
              ) : null}
              {manage && m.status === "active" ? (
                <>
                  <SelectField
                    label={t("members.roleFor", { name: m.displayName })}
                    name={`role-${m.userId}`}
                    value={m.role}
                    onChange={(e) => void change(send(base, "PUT", { userId: m.userId, role: e.target.value }), t("members.changed", { name: m.displayName }))}
                    options={roles.map((r) => ({ value: r, label: t(`roles.${r}`) }))}
                  />
                  {m.source === "community" ? (
                    <Button size="sm" variant="quiet" icon="trash" onClick={() => void change(send(`${base}/${m.userId}`, "DELETE"), t("members.removed", { name: m.displayName }))}>
                      {t("members.remove")}
                    </Button>
                  ) : null}
                </>
              ) : null}
            </li>
          ))}
        </ul>
      </section>
      {manage ? (
        <section className="engagement-section" aria-labelledby="engagement-add-member">
          <h2 id="engagement-add-member">{t("members.addTitle")}</h2>
          <PersonPicker label={t("members.person")} value={adding} onChange={setAdding} />
          <div className="engagement-row">
            <SelectField label={t("members.role")} name="new-role" value={role} onChange={(e) => setRole(e.target.value as MemberRole)} options={roles.map((r) => ({ value: r, label: t(`roles.${r}`) }))} />
            <Button
              variant="primary"
              icon="plus"
              disabled={!adding}
              onClick={() => {
                if (!adding) return;
                void change(send(base, "PUT", { userId: adding, role }), t("members.added")).then(() => setAdding(null));
              }}
            >
              {t("members.add")}
            </Button>
          </div>
        </section>
      ) : null}
    </div>
  );
}
