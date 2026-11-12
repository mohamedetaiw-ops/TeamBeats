"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { StateView } from "@/components/ui/StateView";
import { call, send, spaceHref, spacesApi, type SpaceSummary, type SpaceView } from "@/lib/spaces";
import { RoleBadge, VisibilityBadge } from "./parts";
import "./spaces.css";

/**
 * The space directory (spec 029 US1): public and private spaces of the organization and the secret ones the viewer is
 * in, or only the viewer's own. A private space they are not in is listed without a link. What appears comes from the API.
 */
export function Directory({ initial, manages }: { initial: SpaceSummary[]; manages: boolean }) {
  const t = useTranslations("spaces.directory");
  const router = useRouter();
  const [scope, setScope] = useState("");
  const [query, setQuery] = useState("");
  const [spaces, setSpaces] = useState<SpaceSummary[]>(initial);
  const [messages, setMessages] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  async function load(nextScope: string, q: string) {
    setBusy(true);
    const result = await call<SpaceSummary[]>(() => fetch(`${spacesApi}?scope=${nextScope}&q=${encodeURIComponent(q)}`, { cache: "no-store" }), t("failed"));
    setBusy(false);
    if (result.ok) {
      setSpaces(result.value);
      setMessages([]);
    } else setMessages(result.messages);
  }

  async function join(space: SpaceSummary) {
    const result = await call<SpaceView>(() => send(`${spacesApi}/${space.id}:join`, "POST"), t("failed"));
    if (result.ok) router.push(spaceHref(space.id));
    else setMessages(result.messages);
  }

  const scopes = [
    { value: "", label: t("scope.directory") },
    { value: "mine", label: t("scope.mine") },
    ...(manages ? [{ value: "all", label: t("scope.all") }] : []),
  ];

  return (
    <div className="spaces">
      <form
        className="spaces-toolbar"
        role="search"
        onSubmit={(e) => {
          e.preventDefault();
          void load(scope, query);
        }}
      >
        <SegmentedControl
          label={t("scope.label")}
          name="scope"
          options={scopes}
          value={scope}
          onChange={(value) => {
            setScope(value);
            void load(value, query);
          }}
        />
        <FormField label={t("search")} name="q" type="search" value={query} onChange={(e) => setQuery(e.target.value)} />
        <Button type="submit" icon="search" busy={busy} busyLabel={t("searching")}>
          {t("find")}
        </Button>
      </form>
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
      {spaces.length === 0 ? (
        <StateView kind="empty" title={t("emptyTitle")} message={scope === "mine" ? t("emptyMine") : t("empty")} icon="folder" />
      ) : (
        <ul className="spaces-grid" aria-label={t("listLabel")}>
          {spaces.map((space) => (
            <li key={space.id} className="spaces-card">
              <h3>{space.canOpen ? <Link href={spaceHref(space.id)}><bdi>{space.name}</bdi></Link> : <bdi>{space.name}</bdi>}</h3>
              <div className="spaces-row">
                <VisibilityBadge visibility={space.visibility} />
                <RoleBadge role={space.role} />
                {space.status === "archived" ? <span className="spaces-meta">{t("archived")}</span> : null}
              </div>
              {space.description ? (
                <p className="spaces-meta">
                  <bdi>{space.description}</bdi>
                </p>
              ) : null}
              <p className="spaces-meta">
                {t(`kind.${space.kind}`)} · {t("members", { count: space.memberCount })}
              </p>
              {!space.canOpen ? <p className="spaces-meta">{t("privateHint")}</p> : null}
              {space.canOpen && !space.role && space.visibility === "public" && space.status === "active" ? (
                <Button size="sm" onClick={() => void join(space)}>
                  {t("join")}
                </Button>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
