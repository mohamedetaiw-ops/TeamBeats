"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { SelectField } from "@/components/ui/FormField";
import { Switch } from "@/components/ui/Switch";
import { problemMessages } from "@/lib/notifications/types";
import {
  importHref,
  nameIn,
  type ConnectedSource,
  type DocumentType,
  type FolderView,
  type ImportView,
  type RemoteColumn,
  type RemoteFolder,
} from "@/lib/documents/types";
import "./documents.css";

const sourceApi = (connectionId: string, endpoint: string) =>
  `/api/v1/documents/connected-sources/${encodeURIComponent(connectionId)}/${endpoint}`;

/**
 * Library managers import a SharePoint document library, or one folder of it, the connection allows (spec 011 FR-033):
 * every version or only the latest, a document type, and which SharePoint column fills which field. The API checks
 * each choice again before anything is queued.
 */
export function SharePointImportStart({
  libraryId,
  folders,
  sources,
  types,
}: {
  libraryId: string;
  folders: FolderView[];
  sources: ConnectedSource[];
  types: DocumentType[];
}) {
  const t = useTranslations("documents.imports.sharepoint");
  const locale = useLocale();
  const router = useRouter();
  const choices = sources
    .filter((s) => s.kind === "microsoft365")
    .flatMap((s) =>
      s.containers.map((c) => ({
        value: `${s.connectionId}|${c.id}`,
        label: c.name,
      })),
    );
  const [container, setContainer] = useState(choices[0]?.value ?? "");
  const [path, setPath] = useState<RemoteFolder[]>([]);
  const [children, setChildren] = useState<Record<string, RemoteFolder[]>>({});
  const [columns, setColumns] = useState<RemoteColumn[]>([]);
  const [allVersions, setAllVersions] = useState(true);
  const [typeId, setTypeId] = useState("");
  const [map, setMap] = useState<Record<string, string>>({});
  const [folder, setFolder] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [connectionId, containerId] = container.split("|");
  const type = types.find((x) => x.id === typeId) ?? null;

  // The columns of the chosen document library, for the mapping.
  useEffect(() => {
    if (!connectionId || !containerId) return;
    let live = true;
    void (async () => {
      try {
        const response = await fetch(
          `${sourceApi(connectionId, "columns")}?container=${encodeURIComponent(containerId)}`,
        );
        if (live && response.ok)
          setColumns((await response.json()) as RemoteColumn[]);
        else if (live) setMessages(await problemMessages(response));
      } catch {
        if (live) setMessages([t("failed")]);
      }
    })();
    return () => {
      live = false;
    };
  }, [connectionId, containerId, t]);

  // The folders inside the last chosen folder (or the library), loaded once each.
  const parent = path.at(-1)?.id ?? "";
  useEffect(() => {
    if (!connectionId || !containerId || children[parent] !== undefined) return;
    let live = true;
    void (async () => {
      const query = new URLSearchParams({ container: containerId });
      if (parent) query.set("folder", parent);
      try {
        const response = await fetch(
          `${sourceApi(connectionId, "folders")}?${query}`,
        );
        const list = response.ok
          ? ((await response.json()) as RemoteFolder[])
          : [];
        if (live) setChildren((c) => ({ ...c, [parent]: list }));
      } catch {
        if (live) setChildren((c) => ({ ...c, [parent]: [] }));
      }
    })();
    return () => {
      live = false;
    };
  }, [connectionId, containerId, parent, children]);

  if (choices.length === 0) {
    return (
      <section
        className="documents-card"
        aria-labelledby="sharepoint-import-title"
      >
        <h2 id="sharepoint-import-title">{t("title")}</h2>
        <p className="documents-meta">
          {t(
            sources.some((s) => s.kind === "microsoft365" && s.state !== "ok")
              ? "unavailable"
              : "none",
          )}
        </p>
      </section>
    );
  }

  async function start() {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(
        `/api/v1/documents/libraries/${encodeURIComponent(libraryId)}/imports/sharepoint`,
        {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({
            connectionId,
            containerId,
            folderId: path.at(-1)?.id ?? null,
            folder: folder || null,
            allVersions,
            typeId: typeId || null,
            columns: type
              ? Object.fromEntries(
                  Object.entries(map).filter(([, field]) => field),
                )
              : {},
          }),
        },
      );
      if (response.ok) {
        const view = (await response.json()) as ImportView;
        router.push(importHref(view.id));
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  // One picker per level: the library's folders, then the folders inside each chosen one.
  const levels = [
    { parent: "", chosen: path[0] },
    ...path.map((f, i) => ({ parent: f.id, chosen: path[i + 1] })),
  ].filter((l) => (children[l.parent]?.length ?? 0) > 0);
  const fieldOptions = [
    { value: "", label: t("noField") },
    ...(type?.fields ?? []).map((f) => ({
      value: f.key,
      label: nameIn(f.names, locale),
    })),
  ];

  return (
    <form
      className="documents-card"
      noValidate
      aria-labelledby="sharepoint-import-title"
      onSubmit={(e) => {
        e.preventDefault();
        void start();
      }}
    >
      <h2 id="sharepoint-import-title">{t("title")}</h2>
      <p>{t("intro")}</p>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <SelectField
        label={t("source")}
        name="sharepoint-source"
        value={container}
        onChange={(e) => {
          setContainer(e.target.value);
          setColumns([]);
          setMap({});
          setPath([]);
          setChildren({});
        }}
        options={choices}
      />
      {levels.map((l, i) => (
        <SelectField
          key={l.parent || "root"}
          label={
            i === 0
              ? t("remoteFolder")
              : t("insideFolder", { folder: path[i - 1]?.name ?? "" })
          }
          name={`sharepoint-folder-${i}`}
          value={l.chosen?.id ?? ""}
          onChange={(e) => {
            const next = children[l.parent]?.find(
              (f) => f.id === e.target.value,
            );
            setPath([...path.slice(0, i), ...(next ? [next] : [])]);
          }}
          options={[
            {
              value: "",
              label: i === 0 ? t("wholeLibrary") : t("wholeFolder"),
            },
            ...(children[l.parent] ?? []).map((f) => ({
              value: f.id,
              label: f.name,
            })),
          ]}
        />
      ))}
      <Switch
        label={t("allVersions")}
        hint={t("allVersionsHint")}
        checked={allVersions}
        onChange={(e) => setAllVersions(e.target.checked)}
      />
      <SelectField
        label={t("type")}
        name="sharepoint-type"
        value={typeId}
        onChange={(e) => {
          setTypeId(e.target.value);
          setMap({});
        }}
        options={[
          { value: "", label: t("noType") },
          ...types
            .filter((x) => x.status === "active")
            .map((x) => ({ value: x.id, label: nameIn(x.names, locale) })),
        ]}
      />
      {type && columns.length > 0 ? (
        <fieldset className="documents-form">
          <legend>{t("columns")}</legend>
          <p className="documents-meta">{t("columnsHint")}</p>
          {columns.map((c) => (
            <SelectField
              key={c.name}
              label={c.displayName}
              name={`sharepoint-column-${c.name}`}
              value={map[c.name] ?? ""}
              onChange={(e) =>
                setMap((m) => ({ ...m, [c.name]: e.target.value }))
              }
              options={fieldOptions}
            />
          ))}
        </fieldset>
      ) : null}
      <SelectField
        label={t("folder")}
        name="sharepoint-target"
        value={folder}
        onChange={(e) => setFolder(e.target.value)}
        options={[
          { value: "", label: t("libraryRoot") },
          ...folders.map((f) => ({ value: f.id, label: f.name })),
        ]}
      />
      <p className="documents-meta">{t("rules")}</p>
      <Button
        type="submit"
        variant="primary"
        disabled={!container}
        busy={busy}
        busyLabel={t("starting")}
      >
        {t("start")}
      </Button>
    </form>
  );
}
