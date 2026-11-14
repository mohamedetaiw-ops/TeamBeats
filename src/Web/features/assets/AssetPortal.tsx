"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { problemMessages } from "@/lib/notifications/types";
import { nameIn } from "@/lib/documents/types";
import {
  assetsApi,
  assetsHref,
  renditionOrder,
  type Asset,
  type AssetAccessRequest,
  type AssetCollection,
  type AssetDownloadReport,
} from "@/lib/assets/types";
import "./assets.css";

function Problems({ messages }: { messages: string[] }) {
  return messages.length > 0 ? (
    <div className="notice notice-error" role="alert">
      {messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  ) : null;
}

/** Sends one change to the asset API, refreshes the page when it worked, and shows the API's problems otherwise. */
function useSend() {
  const t = useTranslations("assets");
  const router = useRouter();
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  async function send(key: string, url: string, init: RequestInit): Promise<boolean> {
    setBusy(key);
    setMessages([]);
    try {
      const response = await fetch(url, init);
      if (response.ok) {
        router.refresh();
        return true;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(null);
    }
    return false;
  }
  const json = (method: "POST" | "PUT", body: unknown): RequestInit => ({
    method,
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  return { busy, messages, send, json };
}

/** The collections, each a link that opens it; a restricted one the reader cannot see says so. */
export function CollectionList({ collections, current }: { collections: AssetCollection[]; current: string | null }) {
  const t = useTranslations("assets");
  const locale = useLocale();
  return (
    <nav className="assets-collections" aria-label={t("collections")}>
      <ul>
        {collections.map((c) => (
          <li key={c.id} data-testid="asset-collection">
            <Link href={`${assetsHref}?collection=${encodeURIComponent(c.id)}`} aria-current={c.id === current ? "page" : undefined}>
              <bdi>{nameIn(c.names, locale)}</bdi>
            </Link>
            {!c.canRead ? <Badge tone="neutral" icon="lock">{t("restricted")}</Badge> : null}
            {c.status === "retired" ? <Badge tone="neutral">{t("retired")}</Badge> : null}
          </li>
        ))}
      </ul>
    </nav>
  );
}

/** A restricted collection: the reader says why they need it and the request goes to the brand managers. */
export function RequestAccess({ collection }: { collection: AssetCollection }) {
  const t = useTranslations("assets");
  const { busy, messages, send, json } = useSend();
  const [reason, setReason] = useState("");
  if (collection.accessRequested) {
    return (
      <p className="notice" data-testid="access-requested">
        {t("requestPending")}
      </p>
    );
  }
  return (
    <form
      className="assets-card"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void send("request", `${assetsApi}/collections/${encodeURIComponent(collection.id)}/access-requests`, json("POST", { reason }));
      }}
    >
      <h2>{t("requestTitle")}</h2>
      <p>{t("requestIntro")}</p>
      <Problems messages={messages} />
      <TextAreaField label={t("reason")} name="reason" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} required />
      <Button type="submit" variant="primary" busy={busy === "request"} busyLabel={t("busy")}>
        {t("requestAccess")}
      </Button>
    </form>
  );
}

const renditionRank = (key: string) => renditionOrder.indexOf(key as (typeof renditionOrder)[number]);

/** The assets of a collection: a preview, usage notes, rights, and a download for each rendition. */
export function AssetGrid({ assets, manage }: { assets: Asset[]; manage: boolean }) {
  const t = useTranslations("assets");
  const locale = useLocale();
  const format = useFormatter();
  const { busy, messages, send, json } = useSend();
  if (assets.length === 0) {
    return <p className="assets-meta">{t("noAssets")}</p>;
  }
  return (
    <>
      <Problems messages={messages} />
      <ul className="assets-grid">
        {assets.map((a) => {
          const name = nameIn(a.names, locale);
          const notes = nameIn(a.usageNotes, locale);
          const thumbnail = a.renditions.some((r) => r.key === "png_256" && r.ready);
          return (
            <li key={a.id} className="assets-card" data-testid="asset">
              {thumbnail ? (
                // eslint-disable-next-line @next/next/no-img-element -- thumbnails come from the authenticated API
                <img className="assets-thumb" src={`${assetsApi}/${encodeURIComponent(a.id)}/thumbnail`} alt={name} />
              ) : null}
              <h3>
                <bdi>{name}</bdi>
              </h3>
              {a.expired ? <Badge tone="warning">{t("expired")}</Badge> : null}
              {a.status === "retired" ? <Badge tone="neutral">{t("retired")}</Badge> : null}
              {notes ? (
                <p className="assets-notes">
                  <bdi>{notes}</bdi>
                </p>
              ) : null}
              <p className="assets-meta">
                {a.rightsExpireAt
                  ? t("rightsUntil", { date: format.dateTime(new Date(a.rightsExpireAt), { dateStyle: "medium" }) })
                  : t("rightsOpen")}
              </p>
              <ul className="assets-renditions" aria-label={t("downloadsFor", { name })}>
                {[...a.renditions]
                  .filter((r) => r.ready)
                  .sort((x, y) => renditionRank(x.key) - renditionRank(y.key))
                  .map((r) => (
                    <li key={r.key}>
                      <a href={`${assetsApi}/${encodeURIComponent(a.id)}/renditions/${r.key}`} download>
                        {t(`rendition.${r.key}`)}
                      </a>
                    </li>
                  ))}
              </ul>
              {manage && a.renditions.some((r) => !r.ready) ? (
                <Button
                  variant="quiet"
                  busy={busy === `render-${a.id}`}
                  busyLabel={t("busy")}
                  onClick={() => void send(`render-${a.id}`, `${assetsApi}/${encodeURIComponent(a.id)}/renditions`, { method: "POST" })}
                >
                  {t("renderAgain")}
                </Button>
              ) : null}
              {manage ? (
                <Button
                  variant="quiet"
                  busy={busy === `status-${a.id}`}
                  busyLabel={t("busy")}
                  aria-label={a.status === "active" ? t("retireNamed", { name }) : t("restoreNamed", { name })}
                  onClick={() =>
                    void send(
                      `status-${a.id}`,
                      `${assetsApi}/${encodeURIComponent(a.id)}`,
                      json("PUT", { names: a.names, usageNotes: a.usageNotes, status: a.status === "active" ? "retired" : "active", version: a.version }),
                    )
                  }
                >
                  {a.status === "active" ? t("retire") : t("restore")}
                </Button>
              ) : null}
            </li>
          );
        })}
      </ul>
    </>
  );
}

/** Brand managers add a collection, open to everyone or restricted to an org unit. */
export function NewCollection({ units }: { units: { id: string; name: string }[] }) {
  const t = useTranslations("assets");
  const locale = useLocale();
  const { busy, messages, send, json } = useSend();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [unit, setUnit] = useState("");
  return (
    <form
      className="assets-card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const done = await send(
          "collection",
          `${assetsApi}/collections`,
          json("POST", {
            names: { [locale]: name },
            descriptions: description ? { [locale]: description } : {},
            restrictedTo: unit || null,
          }),
        );
        if (done) {
          setName("");
          setDescription("");
        }
      }}
    >
      <h2>{t("newCollection")}</h2>
      <Problems messages={messages} />
      <FormField label={t("collectionName")} name="collection-name" value={name} maxLength={120} required onChange={(e) => setName(e.target.value)} />
      <TextAreaField label={t("description")} name="collection-description" value={description} maxLength={2000} onChange={(e) => setDescription(e.target.value)} />
      <SelectField
        label={t("audience")}
        name="collection-audience"
        value={unit}
        onChange={(e) => setUnit(e.target.value)}
        options={[{ value: "", label: t("everyone") }, ...units.map((u) => ({ value: u.id, label: t("onlyUnit", { unit: u.name }) }))]}
      />
      <Button type="submit" variant="primary" busy={busy === "collection"} busyLabel={t("busy")}>
        {t("createCollection")}
      </Button>
    </form>
  );
}

/** Brand managers upload an asset with its title, usage notes and when its usage rights end. */
export function UploadAsset({ collection }: { collection: AssetCollection }) {
  const t = useTranslations("assets");
  const locale = useLocale();
  const { busy, messages, send } = useSend();
  const [file, setFile] = useState<File | null>(null);
  const [title, setTitle] = useState("");
  const [notes, setNotes] = useState("");
  const [expiry, setExpiry] = useState("");
  const items: UploadItem[] = file ? [{ key: "file", name: file.name, size: file.size }] : [];
  return (
    <form
      className="assets-card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (!file) return;
        const query = new URLSearchParams({ name: file.name });
        if (title) query.set("title", title);
        if (notes) query.set("notes", notes);
        if (expiry) query.set("rightsExpireAt", new Date(`${expiry}T23:59:59`).toISOString());
        const done = await send("upload", `${assetsApi}/collections/${encodeURIComponent(collection.id)}/assets?${query}`, {
          method: "POST",
          headers: { "content-type": "application/octet-stream", "accept-language": locale },
          body: file,
        });
        if (done) {
          setFile(null);
          setTitle("");
          setNotes("");
          setExpiry("");
        }
      }}
    >
      <h2>{t("upload")}</h2>
      <Problems messages={messages} />
      <FileUpload
        label={t("file")}
        hint={t("fileHint")}
        accept=".png,.jpg,.jpeg,.gif,.webp,.svg,.pdf,.docx,.pptx,.potx,.xlsx"
        multiple={false}
        items={items}
        onAdd={(files) => setFile(files[0] ?? null)}
        onRemove={() => setFile(null)}
      />
      <FormField label={t("assetTitle")} name="asset-title" value={title} maxLength={120} onChange={(e) => setTitle(e.target.value)} />
      <TextAreaField label={t("usageNotes")} name="asset-notes" value={notes} maxLength={2000} onChange={(e) => setNotes(e.target.value)} />
      <FormField label={t("rightsExpire")} hint={t("rightsExpireHint")} name="asset-expiry" type="date" value={expiry} onChange={(e) => setExpiry(e.target.value)} />
      <Button type="submit" variant="primary" disabled={!file} busy={busy === "upload"} busyLabel={t("busy")}>
        {t("uploadButton")}
      </Button>
    </form>
  );
}

/** Pending access requests for brand managers, with the last ones decided. */
export function AccessRequests({ requests }: { requests: AssetAccessRequest[] }) {
  const t = useTranslations("assets");
  const locale = useLocale();
  const format = useFormatter();
  const { busy, messages, send, json } = useSend();
  return (
    <section className="assets-card" aria-labelledby="asset-requests-title">
      <h2 id="asset-requests-title">{t("requests")}</h2>
      <Problems messages={messages} />
      {requests.length === 0 ? (
        <p className="assets-meta">{t("noRequests")}</p>
      ) : (
        <ul className="assets-requests">
          {requests.map((r) => (
            <li key={r.id} data-testid="access-request">
              <p>
                <strong>
                  <bdi>{r.userName}</bdi>
                </strong>{" "}
                {t("asksFor", { collection: nameIn(r.collectionNames, locale) })}
              </p>
              <p className="assets-notes">
                <bdi>{r.reason}</bdi>
              </p>
              <p className="assets-meta">
                <time dateTime={r.createdAt}>{format.dateTime(new Date(r.createdAt), { dateStyle: "medium" })}</time>
              </p>
              {r.status === "pending" ? (
                <div className="assets-actions">
                  <Button
                    variant="primary"
                    busy={busy === `approve-${r.id}`}
                    busyLabel={t("busy")}
                    aria-label={t("approveNamed", { name: r.userName })}
                    onClick={() => void send(`approve-${r.id}`, `${assetsApi}/access-requests/${encodeURIComponent(r.id)}`, json("POST", { decision: "approve" }))}
                  >
                    {t("approve")}
                  </Button>
                  <Button
                    busy={busy === `decline-${r.id}`}
                    busyLabel={t("busy")}
                    aria-label={t("declineNamed", { name: r.userName })}
                    onClick={() => void send(`decline-${r.id}`, `${assetsApi}/access-requests/${encodeURIComponent(r.id)}`, json("POST", { decision: "decline" }))}
                  >
                    {t("decline")}
                  </Button>
                </div>
              ) : (
                <Badge tone={r.status === "approved" ? "success" : "neutral"}>{t(`status.${r.status}`)}</Badge>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Who downloaded which asset and rendition in the last 90 days (US5 scenario 4). */
export function DownloadReport({ report }: { report: AssetDownloadReport }) {
  const t = useTranslations("assets");
  const locale = useLocale();
  return (
    <section className="assets-card" aria-labelledby="asset-downloads-title">
      <h2 id="asset-downloads-title">{t("downloads")}</h2>
      {report.totals.length === 0 ? (
        <p className="assets-meta">{t("noDownloads")}</p>
      ) : (
        <div className="assets-table">
          <table>
            <caption className="visually-hidden">{t("downloadsCaption")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("asset")}</th>
                <th scope="col">{t("renditionLabel")}</th>
                <th scope="col">{t("downloadCount")}</th>
                <th scope="col">{t("people")}</th>
              </tr>
            </thead>
            <tbody>
              {report.totals.map((row) => (
                <tr key={`${row.assetId}-${row.rendition}`} data-testid="download-total">
                  <td>
                    <bdi>{nameIn(row.assetNames, locale)}</bdi>
                  </td>
                  <td>{t(`rendition.${row.rendition}`)}</td>
                  <td>{row.downloads}</td>
                  <td>{row.people}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
