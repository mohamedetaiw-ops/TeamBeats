"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FormField, SelectField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import type {
  FolderView,
  GuestShare,
  LibraryView,
} from "@/lib/documents/types";

export type SpaceChoice = { id: string; name: string };

/**
 * Download and sharing controls of a TeamBeats library (spec 011 US8): view only (readers preview with a watermark and
 * never download) and sharing folders read-only with guest spaces for a number of days. Only the library's managers
 * see this; the API decides what each setting needs.
 */
export function LibrarySharing({
  library,
  folders,
  shares,
  spaces,
}: {
  library: LibraryView;
  folders: FolderView[];
  shares: GuestShare[];
  spaces: SpaceChoice[];
}) {
  const t = useTranslations("documents.sharing");
  const format = useFormatter();
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [saved, setSaved] = useState<string | null>(null);
  const [folder, setFolder] = useState(folders[0]?.id ?? "");
  const [space, setSpace] = useState(spaces[0]?.id ?? "");
  const [days, setDays] = useState("30");

  async function send(
    key: string,
    url: string,
    method: "POST" | "PUT",
    body: unknown,
    done: string,
  ) {
    setBusy(key);
    setMessages([]);
    setSaved(null);
    try {
      const response = await fetch(url, {
        method,
        headers: body === undefined ? undefined : { "content-type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (response.ok) {
        setSaved(done);
        router.refresh();
      } else {
        setMessages(await problemMessages(response));
      }
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
  }

  function setting(change: { viewOnly?: boolean; guestSharing?: boolean }) {
    void send(
      "settings",
      `/api/v1/documents/libraries/${encodeURIComponent(library.id)}`,
      "PUT",
      {
        names: library.names,
        allowedExtensions: library.allowedExtensions ?? null,
        maxFileMb: library.maxFileMb ?? null,
        classificationDefault: library.classificationDefault,
        version: library.version,
        ...change,
      },
      t("settingsSaved"),
    );
  }

  const date = (value: string) =>
    format.dateTime(new Date(value), { dateStyle: "medium" });

  return (
    <section className="documents-card documents-sharing" aria-labelledby="library-sharing-title">
      <h2 id="library-sharing-title">{t("title")}</h2>
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      {saved ? (
        <p className="notice" role="status">
          {saved}
        </p>
      ) : null}
      <Checkbox
        label={t("viewOnly")}
        hint={t("viewOnlyHint")}
        checked={library.viewOnly ?? false}
        disabled={busy !== null}
        onChange={(e) => setting({ viewOnly: e.target.checked })}
      />
      <Checkbox
        label={t("guestSharing")}
        hint={t("guestSharingHint")}
        checked={library.guestSharing ?? false}
        disabled={busy !== null}
        onChange={(e) => setting({ guestSharing: e.target.checked })}
      />

      {library.guestSharing ? (
        <>
          <h3>{t("shareTitle")}</h3>
          {folders.length === 0 || spaces.length === 0 ? (
            <p className="documents-meta">
              {folders.length === 0 ? t("noFolders") : t("noSpaces")}
            </p>
          ) : (
            <form
              className="documents-sharing-form"
              noValidate
              onSubmit={(e) => {
                e.preventDefault();
                void send(
                  "share",
                  `/api/v1/documents/libraries/${encodeURIComponent(library.id)}/guest-shares`,
                  "POST",
                  { folderId: folder, spaceId: space, days: Number(days) },
                  t("shared"),
                );
              }}
            >
              <SelectField
                label={t("folder")}
                name="share-folder"
                value={folder}
                onChange={(e) => setFolder(e.target.value)}
                options={folders.map((f) => ({ value: f.id, label: f.name }))}
              />
              <SelectField
                label={t("space")}
                name="share-space"
                hint={t("spaceHint")}
                value={space}
                onChange={(e) => setSpace(e.target.value)}
                options={spaces.map((s) => ({ value: s.id, label: s.name }))}
              />
              <FormField
                label={t("days")}
                name="share-days"
                type="number"
                inputMode="numeric"
                min={1}
                max={365}
                value={days}
                onChange={(e) => setDays(e.target.value)}
              />
              <Button type="submit" variant="primary" busy={busy === "share"} busyLabel={t("busy")}>
                {t("share")}
              </Button>
            </form>
          )}
        </>
      ) : null}

      {shares.length > 0 ? (
        <ul className="documents-sharing-list">
          {shares.map((s) => (
            <li key={s.id} data-testid="guest-share">
              <span>
                <bdi>{s.folderName}</bdi>
                {" · "}
                <bdi>{s.spaceName}</bdi>
              </span>
              <span className="documents-meta">
                {s.live
                  ? t("until", { date: date(s.expiresAt) })
                  : s.revokedAt
                    ? t("revokedOn", { date: date(s.revokedAt) })
                    : t("expiredOn", { date: date(s.expiresAt) })}
              </span>
              {s.live ? (
                <Button
                  variant="quiet"
                  busy={busy === `revoke-${s.id}`}
                  busyLabel={t("busy")}
                  aria-label={t("revokeNamed", { folder: s.folderName, space: s.spaceName })}
                  onClick={() =>
                    void send(
                      `revoke-${s.id}`,
                      `/api/v1/documents/guest-shares/${encodeURIComponent(s.id)}/revoke`,
                      "POST",
                      undefined,
                      t("revoked"),
                    )
                  }
                >
                  {t("revoke")}
                </Button>
              ) : (
                <Badge>{s.revokedAt ? t("revokedBadge") : t("expiredBadge")}</Badge>
              )}
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
