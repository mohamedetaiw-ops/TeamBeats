"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField } from "@/components/ui/FormField";
import { personName, type PersonCard } from "@/lib/people/types";
import { call, roles, send, spacesApi, spacesHref, type SpaceMember, type SpaceView } from "@/lib/spaces";
import { RoleBadge } from "./parts";
import "./spaces.css";

/**
 * Members of a space (spec 029 FR-002, US1 scenario 3). Owners add people from the directory, change roles and remove
 * them; anyone in the space may leave, and a public space that allows it can be joined. A space always keeps one owner,
 * which the API enforces.
 */
export function Members({ space, initial }: { space: SpaceView; initial: SpaceMember[] }) {
  const t = useTranslations("spaces.members");
  const locale = useLocale();
  const router = useRouter();
  const [members, setMembers] = useState(initial);
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<PersonCard[] | null>(null);
  const [role, setRole] = useState("member");
  const [messages, setMessages] = useState<string[]>([]);
  const url = `${spacesApi}/${space.id}`;
  const manage = (space.rights.owns || space.rights.manages) && space.status === "active";

  async function search() {
    if (!query.trim()) return;
    const result = await call<{ items: PersonCard[] }>(() => fetch(`/api/v1/people?q=${encodeURIComponent(query.trim())}&limit=8`, { cache: "no-store" }), t("failed"));
    setFound(result.ok ? result.value.items : []);
  }

  async function setMember(userId: string, newRole: string) {
    const result = await call<SpaceMember[]>(() => send(`${url}/members`, "POST", { userId, role: newRole }), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setMembers(result.value);
    setFound(null);
    setQuery("");
  }

  async function remove(userId: string) {
    const result = await call<void>(() => send(`${url}/members/${userId}`, "DELETE"), t("failed"));
    if (!result.ok) return setMessages(result.messages);
    setMessages([]);
    setMembers((list) => list.filter((m) => m.userId !== userId));
  }

  async function leave() {
    const result = await call<void>(() => send(`${url}:leave`, "POST"), t("failed"));
    if (result.ok) router.push(spacesHref);
    else setMessages(result.messages);
  }

  async function join() {
    const result = await call<SpaceView>(() => send(`${url}:join`, "POST"), t("failed"));
    if (result.ok) router.refresh();
    else setMessages(result.messages);
  }

  return (
    <div className="spaces">
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      <div className="spaces-row">
        {space.rights.canJoin ? (
          <Button variant="primary" onClick={() => void join()}>
            {t("join")}
          </Button>
        ) : null}
        {space.rights.canLeave ? <Button onClick={() => void leave()}>{t("leave")}</Button> : null}
      </div>

      <section className="spaces-section" aria-labelledby="members-list">
        <h2 id="members-list">{t("title", { count: members.length })}</h2>
        <ul className="spaces-list" aria-label={t("listLabel")}>
          {members.map((m) => (
            <li key={m.userId} className="spaces-member">
              <bdi>{m.displayName}</bdi>
              {manage ? (
                <>
                  <SelectField label={t("roleOf", { name: m.displayName })} name={`role-${m.userId}`} value={m.role}
                    onChange={(e) => void setMember(m.userId, e.target.value)} options={roles.map((r) => ({ value: r, label: t(`role.${r}`) }))} />
                  <Button size="sm" variant="quiet" onClick={() => void remove(m.userId)}>
                    {t("remove")}
                  </Button>
                </>
              ) : (
                <RoleBadge role={m.role} guest={m.guest} />
              )}
            </li>
          ))}
        </ul>
      </section>

      {manage ? (
        <section className="spaces-section" aria-labelledby="members-add">
          <h2 id="members-add">{t("addTitle")}</h2>
          <form
            className="spaces-toolbar"
            role="search"
            onSubmit={(e) => {
              e.preventDefault();
              void search();
            }}
          >
            <FormField label={t("search")} name="q" type="search" value={query} onChange={(e) => setQuery(e.target.value)} />
            <SelectField label={t("newRole")} name="newRole" value={role} onChange={(e) => setRole(e.target.value)}
              options={roles.map((r) => ({ value: r, label: t(`role.${r}`) }))} />
            <Button type="submit" icon="search">
              {t("find")}
            </Button>
          </form>
          {found ? (
            found.length === 0 ? (
              <p className="spaces-meta">{t("noMatch")}</p>
            ) : (
              <ul className="spaces-list" aria-label={t("results")}>
                {found.map((p) => (
                  <li key={p.id} className="spaces-row">
                    <bdi>{personName(p, locale)}</bdi>
                    {p.jobTitle ? <span className="spaces-meta">{p.jobTitle}</span> : null}
                    <Button size="sm" onClick={() => void setMember(p.id, role)}>
                      {members.some((m) => m.userId === p.id) ? t("change") : t("add")}
                    </Button>
                  </li>
                ))}
              </ul>
            )
          ) : null}
        </section>
      ) : null}
    </div>
  );
}
