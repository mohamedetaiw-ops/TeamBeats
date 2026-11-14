"use client";

import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { dateTimeFull, relativeTime } from "@/lib/format";
import { useNow } from "@/lib/clock";
import { connectHref } from "@/components/experience/outlook";
import {
  connectedContentHref,
  type ConnectedPage,
  type LibraryView,
} from "@/lib/documents/types";
import Link from "next/link";
import { Badge } from "@/components/ui/Badge";
import { Breadcrumb } from "@/components/ui/Breadcrumb";
import { Button } from "@/components/ui/Button";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { FormField } from "@/components/ui/FormField";
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import { Switch } from "@/components/ui/Switch";
import "./documents.css";

type Answer = "loading" | "failed" | ConnectedPage;

/** The outcome of a write as the person (FR-029): ok, or why SharePoint or TeamBeats did not write it. */
type WriteAnswer = { state: string };
const WRITE_STATES = ["forbidden", "conflict", "not_found", "too_large", "unavailable", "not_connected", "reconnect"] as const;

async function write(url: string, init: RequestInit): Promise<string> {
  try {
    const response = await fetch(url, init);
    if (response.status === 403) return "forbidden";
    if (!response.ok) return "failed";
    return ((await response.json()) as WriteAnswer).state;
  } catch {
    return "failed";
  }
}

async function fetchPage(base: string, folder: string | null, cursor: string | null): Promise<"failed" | ConnectedPage> {
  const query = new URLSearchParams();
  if (folder) query.set("folder", folder);
  if (cursor) query.set("cursor", cursor);
  try {
    const response = await fetch(`${base}?${query.toString()}`);
    return response.ok ? ((await response.json()) as ConnectedPage) : "failed";
  } catch {
    return "failed";
  }
}

/**
 * A connected library (spec 011 US9, FR-027, FR-030): the files of a SharePoint document library as this person sees
 * them in SharePoint, listed and opened live with their own Microsoft 365 access. TeamBeats-only actions are named as
 * not available, with the reason, instead of being hidden.
 */
export type TrailStep = { id: string; name: string };

/** The folder path lives in the address, so a folder can be shared and the back button works. */
export function trailHref(path: string, trail: readonly TrailStep[]): string {
  return trail.length === 0 ? path : `${path}?trail=${encodeURIComponent(JSON.stringify(trail))}`;
}

export function ConnectedLibraryBrowser({ library, path, trail }: { library: LibraryView; path: string; trail: TrailStep[] }) {
  const t = useTranslations("documents.connected");
  const locale = useLocale();
  const format = useFormatter();
  const now = useNow();
  const [answer, setAnswer] = useState<Answer>("loading");
  const [more, setMore] = useState(false);
  const folder = trail.length > 0 ? trail[trail.length - 1]!.id : null;
  const returnTo = trailHref(path, trail);
  const libraryBase = `/api/v1/documents/libraries/${encodeURIComponent(library.id)}/connected`;
  const base = `${libraryBase}/items`;
  const [uploadEnabled, setUploadEnabled] = useState(library.connected?.uploadEnabled ?? false);
  const [settingBusy, setSettingBusy] = useState(false);
  const [uploads, setUploads] = useState<UploadItem[]>([]);
  const [renaming, setRenaming] = useState<{ id: string; name: string; busy: boolean } | null>(null);
  const [outcome, setOutcome] = useState<{ ok: boolean; text: string } | null>(null);
  const writable = uploadEnabled && library.rights.contribute && library.status === "active";
  const writeMessage = (state: string) =>
    (WRITE_STATES as readonly string[]).includes(state) ? t(`write.${state}`) : t("write.failed");

  useEffect(() => {
    let live = true;
    void fetchPage(base, folder, null).then((page) => {
      if (live) setAnswer(page);
    });
    return () => {
      live = false;
    };
  }, [base, folder]);

  async function retry() {
    setAnswer("loading");
    setAnswer(await fetchPage(base, folder, null));
  }

  async function toggleUploads(on: boolean) {
    setSettingBusy(true);
    setOutcome(null);
    try {
      const response = await fetch(`${libraryBase}/settings`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ uploadEnabled: on }),
      });
      if (response.ok) setUploadEnabled(on);
      else setOutcome({ ok: false, text: t("write.failed") });
    } catch {
      setOutcome({ ok: false, text: t("write.failed") });
    }
    setSettingBusy(false);
  }

  async function upload(files: File[]) {
    setOutcome(null);
    const added = files.map((file, i) => ({ key: `${Date.now()}-${i}-${file.name}`, name: file.name, size: file.size, progress: 0 }));
    setUploads((all) => [...all, ...added]);
    let any = false;
    for (const [i, file] of files.entries()) {
      const query = new URLSearchParams({ name: file.name });
      if (folder) query.set("folder", folder);
      const state = await write(`${libraryBase}/files?${query.toString()}`, { method: "POST", body: file });
      const key = added[i]!.key;
      if (state === "ok") {
        any = true;
        setUploads((all) => all.filter((u) => u.key !== key));
      } else {
        setUploads((all) => all.map((u) => (u.key === key ? { ...u, progress: undefined, error: writeMessage(state) } : u)));
      }
    }
    if (any) {
      setOutcome({ ok: true, text: t("write.uploaded") });
      await retry();
    }
  }

  async function rename() {
    if (!renaming) return;
    setRenaming({ ...renaming, busy: true });
    const state = await write(`${libraryBase}/rename`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ item: renaming.id, name: renaming.name }),
    });
    if (state === "ok") {
      setRenaming(null);
      setOutcome({ ok: true, text: t("write.renamed") });
      await retry();
    } else {
      setRenaming({ ...renaming, busy: false });
      setOutcome({ ok: false, text: writeMessage(state) });
    }
  }

  const size = (bytes: number | null) => (bytes == null ? "" : format.number(bytes / 1024, { maximumFractionDigits: 0 }) + " " + t("kb"));

  let body;
  if (answer === "loading") {
    body = <StateView kind="loading" minBlockSize="10rem" />;
  } else if (answer === "failed" || answer.state === "unavailable") {
    body = (
      <StateView
        kind="error"
        title={t("unavailable")}
        message={t("unavailableHint")}
        action={
          <Button onClick={() => void retry()}>
            {t("retry")}
          </Button>
        }
      />
    );
  } else if (answer.state === "not_connected" || answer.state === "reconnect") {
    body = (
      <StateView
        kind="denied"
        title={t(answer.state === "reconnect" ? "reconnect" : "connect")}
        message={t("connectHint")}
        action={
          <a className="button button-primary" href={connectHref(returnTo)}>
            {t("connectAction")}
          </a>
        }
      />
    );
  } else if (answer.state !== "ok" && answer.state !== "degraded") {
    body = <StateView kind="empty" icon="folder" title={t("notFound")} message="" />;
  } else if (answer.items.length === 0) {
    body = <StateView kind="empty" icon="folder" title={t("empty")} message="" />;
  } else {
    body = (
      <>
        {answer.state === "degraded" ? (
          <p className="notice" role="status">
            {t("degraded", { age: answer.syncedAt && now !== null ? relativeTime(answer.syncedAt, locale, now) : "" })}
          </p>
        ) : null}
        <div className="documents-table">
          <table>
            <thead>
              <tr>
                <th scope="col">{t("name")}</th>
                <th scope="col">{t("size")}</th>
                <th scope="col">{t("updated")}</th>
                <th scope="col">
                  <span className="visually-hidden">{t("actions")}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {answer.items.map((item) => (
                <tr key={item.id}>
                  <td>
                    {renaming?.id === item.id ? (
                      <form
                        className="documents-form"
                        onSubmit={(e) => {
                          e.preventDefault();
                          void rename();
                        }}
                      >
                        <FormField
                          label={t("write.newName", { name: item.name })}
                          name="connected-rename"
                          value={renaming.name}
                          maxLength={200}
                          onChange={(e) => setRenaming({ ...renaming, name: e.target.value })}
                        />
                        <Button type="submit" variant="primary" busy={renaming.busy} busyLabel={t("loading")}>
                          {t("write.save")}
                        </Button>
                        <Button onClick={() => setRenaming(null)}>{t("write.cancel")}</Button>
                      </form>
                    ) : item.folder ? (
                      <Link className="documents-name" href={trailHref(path, [...trail, { id: item.id, name: item.name }])}>
                        <Icon name="folder" size={18} />
                        <bdi>{item.name}</bdi>
                      </Link>
                    ) : answer.state === "degraded" ? (
                      <span className="documents-name">
                        <Icon name="files" size={18} />
                        <bdi>{item.name}</bdi>
                      </span>
                    ) : (
                      <a className="documents-name" href={connectedContentHref(library.id, item.id)} download>
                        <Icon name="files" size={18} />
                        <bdi>{item.name}</bdi>
                      </a>
                    )}
                  </td>
                  <td>{item.folder ? "" : size(item.size)}</td>
                  <td>
                    {item.modifiedAt && now !== null ? (
                      <time dateTime={item.modifiedAt} title={dateTimeFull(item.modifiedAt, locale)}>
                        {relativeTime(item.modifiedAt, locale, now)}
                      </time>
                    ) : null}
                  </td>
                  <td>
                    {item.webLink ? (
                      <a href={item.webLink} target="_blank" rel="noreferrer" aria-label={t("openInSharePoint", { name: item.name })}>
                        {t("openInSharePointShort")}
                      </a>
                    ) : null}
                    {writable && answer.state === "ok" && renaming?.id !== item.id ? (
                      <Button variant="quiet" aria-label={t("write.rename", { name: item.name })} onClick={() => setRenaming({ id: item.id, name: item.name, busy: false })}>
                        {t("write.renameShort")}
                      </Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {answer.nextCursor ? (
          <Button
            busy={more}
            busyLabel={t("loading")}
            onClick={async () => {
              setMore(true);
              const next = await fetchPage(base, folder, answer.nextCursor);
              setAnswer(next === "failed" ? next : { ...next, items: [...answer.items, ...next.items] });
              setMore(false);
            }}
          >
            {t("more")}
          </Button>
        ) : null}
      </>
    );
  }

  return (
    <div className="documents-browser" data-testid="connected-library">
      <p className="notice" role="note">
        <Badge tone="info">{t("badge")}</Badge>{" "}
        {t("intro")}
      </p>
      <Breadcrumb
        label={t("path")}
        crumbs={[
          { href: path, label: library.connected?.containerName ?? t("root") },
          ...trail.map((step, i) => ({ href: trailHref(path, trail.slice(0, i + 1)), label: step.name })),
        ]}
      />
      {library.rights.manage && library.status === "active" ? (
        <Switch
          label={t("write.allow")}
          hint={t("write.allowHint")}
          checked={uploadEnabled}
          disabled={settingBusy}
          onChange={(e) => void toggleUploads(e.target.checked)}
        />
      ) : null}
      {writable && answer !== "loading" && answer !== "failed" && answer.state === "ok" ? (
        <FileUpload
          label={t("write.upload")}
          hint={t("write.uploadHint")}
          items={uploads}
          onAdd={(files) => void upload(files)}
          onRemove={(key) => setUploads((all) => all.filter((u) => u.key !== key))}
        />
      ) : null}
      {outcome ? (
        <p className={outcome.ok ? "notice" : "notice notice-error"} role={outcome.ok ? "status" : "alert"}>
          {outcome.text}
        </p>
      ) : null}
      <section aria-label={t("results")} aria-busy={answer === "loading" || undefined}>
        {body}
      </section>
      <section className="documents-card" aria-labelledby="connected-not-here">
        <h2 id="connected-not-here">{t("notHereTitle")}</h2>
        <p>{t("notHere")}</p>
      </section>
    </div>
  );
}
