"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { languageName } from "@/lib/format";
import { problemMessages } from "@/lib/notifications/types";
import type { OrgUnitView } from "@/lib/admin/types";
import { unitLabel } from "@/lib/admin/types";
import { communityHref, engagementApi, localName, send, type CommunityKind, type CommunityView } from "@/lib/engagement";
import "./engagement.css";

const kinds: CommunityKind[] = ["open", "approval", "closed"];

/**
 * The communities directory (spec 023 US1): the communities the API lets this viewer see, theirs first, with join on
 * the open and approval ones. Closed communities appear only to their members; the page shows what the API returns.
 * Engagement administrators also get the form to start a community, optionally linked to an org unit.
 */
export function CommunityDirectory({ initial, canCreate, locales }: { initial: CommunityView[]; canCreate: boolean; locales: string[] }) {
  const t = useTranslations("engagement");
  const locale = useLocale();
  const [list, setList] = useState(initial);
  const [query, setQuery] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);

  async function search(e?: React.FormEvent) {
    e?.preventDefault();
    const response = await fetch(`${engagementApi}/communities?q=${encodeURIComponent(query.trim())}`, { cache: "no-store" });
    if (response.ok) setList((await response.json()) as CommunityView[]);
  }

  async function join(c: CommunityView) {
    setBusy(c.id);
    setMessages([]);
    setNotice(null);
    try {
      const response = await send(`${engagementApi}/communities/${c.id}:join`, "POST");
      if (response.ok) {
        const view = (await response.json()) as CommunityView;
        setList((all) => all.map((x) => (x.id === view.id ? view : x)));
        setNotice(view.rights.pending ? t("directory.requested", { name: localName(view.names, locale) }) : t("directory.joined", { name: localName(view.names, locale) }));
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="engagement">
      <form className="engagement-row" role="search" onSubmit={(e) => void search(e)}>
        <FormField label={t("directory.search")} name="q" type="search" value={query} onChange={(e) => setQuery(e.target.value)} />
        <Button type="submit" icon="search">
          {t("directory.find")}
        </Button>
      </form>
      {notice ? (
        <Alert tone="success" title={notice} live />
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {list.length === 0 ? (
        <StateView kind="empty" title={t("directory.emptyTitle")} message={t("directory.empty")} icon="chat" />
      ) : (
        <ul className="engagement-grid" aria-label={t("directory.listLabel")}>
          {list.map((c) => (
            <li key={c.id}>
              <Card
                title={localName(c.names, locale)}
                href={c.rights.member || c.kind === "open" || c.rights.canModerate || c.rights.canManage ? communityHref(c.id) : undefined}
                eyebrow={
                  <span className="engagement-actions">
                    <Badge tone={c.kind === "open" ? "success" : c.kind === "approval" ? "info" : "neutral"}>{t(`kinds.${c.kind}`)}</Badge>
                    {c.orgUnitId ? <Badge tone="accent">{t("directory.department")}</Badge> : null}
                    {c.rights.member ? <Badge tone="accent">{t("directory.member")}</Badge> : null}
                    {c.rights.pending ? <Badge tone="warning">{t("directory.pending")}</Badge> : null}
                  </span>
                }
                footer={
                  c.rights.canJoin ? (
                    <Button size="sm" variant="primary" busy={busy === c.id} busyLabel={t("working")} onClick={() => void join(c)}>
                      {c.kind === "approval" ? t("community.askToJoin") : t("community.join")}
                    </Button>
                  ) : null
                }
              >
                {localName(c.descriptions, locale) ? <p className="engagement-meta">{localName(c.descriptions, locale)}</p> : null}
                <p className="engagement-meta">{t("directory.members", { count: c.memberCount })}</p>
              </Card>
            </li>
          ))}
        </ul>
      )}
      {canCreate ? <CreateCommunity locales={locales} onCreated={(view) => setList((all) => [view, ...all])} /> : null}
    </div>
  );
}

function CreateCommunity({ locales, onCreated }: { locales: string[]; onCreated: (view: CommunityView) => void }) {
  const t = useTranslations("engagement");
  const locale = useLocale();
  const [names, setNames] = useState<Record<string, string>>({});
  const [descriptions, setDescriptions] = useState<Record<string, string>>({});
  const [kind, setKind] = useState<CommunityKind>("open");
  const [unit, setUnit] = useState("");
  const [units, setUnits] = useState<OrgUnitView[]>([]);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [created, setCreated] = useState<CommunityView | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch("/api/v1/org/units", { cache: "no-store" });
        if (live && response.ok) setUnits(((await response.json()) as OrgUnitView[]).filter((u) => u.status === "active"));
      } catch {
        // Without the unit list the community can still be made without a unit.
      }
    })();
    return () => {
      live = false;
    };
  }, []);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    setCreated(null);
    try {
      const response = await send(`${engagementApi}/communities`, "POST", { names, descriptions, kind, orgUnitId: unit || null });
      if (response.ok) {
        const view = (await response.json()) as CommunityView;
        setCreated(view);
        setNames({});
        setDescriptions({});
        setUnit("");
        onCreated(view);
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
    <section className="engagement-section" aria-labelledby="engagement-create">
      <h2 id="engagement-create">{t("create.title")}</h2>
      <p className="engagement-meta">{t("create.intro")}</p>
      <form className="engagement" onSubmit={(e) => void create(e)}>
        {locales.map((l) => (
          <div key={l} className="engagement-row">
            <FormField
              label={t("create.name", { language: languageName(l, locale) })}
              name={`name-${l}`}
              value={names[l] ?? ""}
              maxLength={120}
              dir="auto"
              onChange={(e) => setNames((all) => ({ ...all, [l]: e.target.value }))}
            />
            <TextAreaField
              label={t("create.description", { language: languageName(l, locale) })}
              name={`description-${l}`}
              value={descriptions[l] ?? ""}
              maxLength={1000}
              rows={2}
              dir="auto"
              onChange={(e) => setDescriptions((all) => ({ ...all, [l]: e.target.value }))}
            />
          </div>
        ))}
        <div className="engagement-row">
          <SelectField
            label={t("create.kind")}
            name="kind"
            value={kind}
            onChange={(e) => setKind(e.target.value as CommunityKind)}
            options={kinds.map((k) => ({ value: k, label: t(`kinds.${k}`) }))}
          />
          <SelectField
            label={t("create.unit")}
            name="unit"
            hint={t("create.unitHint")}
            value={unit}
            onChange={(e) => setUnit(e.target.value)}
            options={[{ value: "", label: t("create.noUnit") }, ...units.map((u) => ({ value: u.id, label: unitLabel(u, locale) }))]}
          />
        </div>
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        {created ? (
          <Alert tone="success" title={t("create.created", { name: localName(created.names, locale) })} live />
        ) : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" icon="plus" busy={busy} busyLabel={t("working")}>
            {t("create.submit")}
          </Button>
        </div>
      </form>
    </section>
  );
}
