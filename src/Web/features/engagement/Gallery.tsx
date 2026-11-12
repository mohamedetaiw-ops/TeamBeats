"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Badge } from "@/components/ui/Badge";
import { Button, ButtonLink } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { FormField, TextAreaField } from "@/components/ui/FormField";
import { Media } from "@/components/ui/Media";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import {
  albumHref,
  engagementApi,
  photoFile,
  send,
  type AlbumList,
  type AlbumPage,
  type AlbumView,
  type EngagementPerson,
  type PhotoView,
} from "@/lib/engagement";
import { MentionPicker } from "./PostCard";
import { call } from "./RecognitionWall";
import "./engagement.css";

/** A community's albums (spec 023 US7): members who may post make an album; everyone who reads the community opens them. */
export function CommunityAlbums({ communityId, initial }: { communityId: string; initial: AlbumList }) {
  const t = useTranslations("engagement.gallery");
  const [albums, setAlbums] = useState(initial.albums);
  return (
    <div className="engagement">
      {initial.canCreate ? <CreateAlbum communityId={communityId} onCreated={(album) => setAlbums((list) => [album, ...list])} /> : null}
      <section className="engagement-section" aria-labelledby="gallery-albums">
        <h2 id="gallery-albums">{t("albumsTitle")}</h2>
        {albums.length === 0 ? (
          <StateView kind="empty" title={t("noAlbumsTitle")} message={t("noAlbums")} icon="image" />
        ) : (
          <ul className="engagement-grid" aria-label={t("albumsTitle")}>
            {albums.map((album) => (
              <li key={album.id}>
                <article className="engagement-post" aria-labelledby={`album-${album.id}`}>
                  {album.coverPhotoId ? <Media src={photoFile(album.coverPhotoId)} ratio="4:3" decorative /> : null}
                  <h3 id={`album-${album.id}`} className="engagement-post-author">
                    <bdi>{album.title}</bdi>
                  </h3>
                  <p className="engagement-meta">{t("photoCount", { count: album.photoCount })}</p>
                  <div className="engagement-row">
                    <ButtonLink size="sm" variant="secondary" href={albumHref(communityId, album.id)}>
                      {t("openAlbum")}
                    </ButtonLink>
                  </div>
                </article>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function CreateAlbum({ communityId, onCreated }: { communityId: string; onCreated: (album: AlbumView) => void }) {
  const t = useTranslations("engagement.gallery");
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [approval, setApproval] = useState(false);
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessages([]);
    const result = await call<AlbumView>(
      () => send(`${engagementApi}/communities/${communityId}/albums`, "POST", { title, description: description || null, requiresApproval: approval }),
      t("failed"),
    );
    if (result.ok) {
      onCreated(result.value);
      setTitle("");
      setDescription("");
      setApproval(false);
    } else {
      setMessages(result.messages);
    }
    setBusy(false);
  }

  return (
    <section className="engagement-section" aria-labelledby="gallery-create">
      <h2 id="gallery-create">{t("createTitle")}</h2>
      <form className="engagement" onSubmit={(e) => void submit(e)}>
        <FormField label={t("albumTitle")} name="album-title" value={title} maxLength={120} dir="auto" onChange={(e) => setTitle(e.target.value)} />
        <TextAreaField label={t("albumDescription")} name="album-description" value={description} maxLength={1000} rows={2} dir="auto" onChange={(e) => setDescription(e.target.value)} />
        <Checkbox label={t("requiresApproval")} hint={t("requiresApprovalHint")} checked={approval} onChange={(e) => setApproval(e.target.checked)} />
        {messages.length > 0 ? (
          <Alert tone="danger" title={t("failed")} live>
            {messages.join(" ")}
          </Alert>
        ) : null}
        <div className="engagement-row">
          <Button type="submit" variant="primary" icon="plus" busy={busy} busyLabel={t("working")}>
            {t("create")}
          </Button>
        </div>
      </form>
    </section>
  );
}

/**
 * One album (spec 023 US7): the photos that show, the upload area for members who may post, and the photos waiting for
 * review, which only their uploader and the moderators see. Whether a photo shows is decided by the API after its
 * malware scan and content safety check.
 */
export function Album({ initial, meId }: { initial: AlbumPage; meId: string }) {
  const t = useTranslations("engagement.gallery");
  const [photos, setPhotos] = useState(initial.photos);
  const [pending, setPending] = useState(initial.pending);

  function placed(view: PhotoView) {
    if (view.status === "visible") setPhotos((list) => [view, ...list.filter((p) => p.id !== view.id)]);
    else setPending((list) => [view, ...list.filter((p) => p.id !== view.id)]);
  }
  function changed(view: PhotoView) {
    setPending((list) => list.filter((p) => p.id !== view.id));
    if (view.status === "visible") setPhotos((list) => (list.some((p) => p.id === view.id) ? list.map((p) => (p.id === view.id ? view : p)) : [view, ...list]));
  }
  function gone(id: string) {
    setPhotos((list) => list.filter((p) => p.id !== id));
    setPending((list) => list.filter((p) => p.id !== id));
  }

  return (
    <div className="engagement">
      {initial.album.description ? <p dir="auto">{initial.album.description}</p> : null}
      {initial.canUpload ? <Upload albumId={initial.album.id} requiresApproval={initial.album.requiresApproval} onUploaded={placed} /> : null}
      {pending.length > 0 ? (
        <section className="engagement-section" aria-labelledby="gallery-pending">
          <h2 id="gallery-pending">{t("pendingTitle")}</h2>
          <ul className="engagement-grid" aria-label={t("pendingTitle")}>
            {pending.map((photo) => (
              <li key={photo.id}>
                <Photo photo={photo} meId={meId} onChanged={changed} onGone={gone} />
              </li>
            ))}
          </ul>
        </section>
      ) : null}
      <section className="engagement-section" aria-labelledby="gallery-photos">
        <h2 id="gallery-photos">{t("photosTitle")}</h2>
        {photos.length === 0 ? (
          <StateView kind="empty" title={t("noPhotosTitle")} message={t("noPhotos")} icon="image" />
        ) : (
          <ul className="engagement-grid" aria-label={t("photosTitle")}>
            {photos.map((photo) => (
              <li key={photo.id} id={`photo-${photo.id}`}>
                <Photo photo={photo} meId={meId} onChanged={changed} onGone={gone} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function Upload({ albumId, requiresApproval, onUploaded }: { albumId: string; requiresApproval: boolean; onUploaded: (view: PhotoView) => void }) {
  const t = useTranslations("engagement.gallery");
  const [items, setItems] = useState<UploadItem[]>([]);
  const [caption, setCaption] = useState("");

  async function add(files: File[]) {
    for (const file of files) {
      const key = `${file.name}-${file.size}-${file.lastModified}`;
      setItems((list) => [...list.filter((i) => i.key !== key), { key, name: file.name, size: file.size, progress: 0 }]);
      let error: string | undefined;
      try {
        const query = caption.trim() ? `?caption=${encodeURIComponent(caption.trim())}` : "";
        const response = await fetch(`${engagementApi}/albums/${albumId}/photos${query}`, { method: "POST", headers: { "content-type": file.type }, body: file });
        if (response.ok) onUploaded((await response.json()) as PhotoView);
        else error = (await problemMessages(response)).join(" ");
      } catch {
        error = t("failed");
      }
      setItems((list) => (error ? list.map((i) => (i.key === key ? { ...i, progress: undefined, error } : i)) : list.filter((i) => i.key !== key)));
    }
    setCaption("");
  }

  return (
    <section className="engagement-section" aria-labelledby="gallery-upload">
      <h2 id="gallery-upload">{t("uploadTitle")}</h2>
      <p className="engagement-meta">{requiresApproval ? t("uploadReviewed") : t("uploadChecked")}</p>
      <FormField label={t("caption")} name="photo-caption" value={caption} maxLength={300} dir="auto" onChange={(e) => setCaption(e.target.value)} />
      <FileUpload
        label={t("choosePhotos")}
        hint={t("uploadHint")}
        accept="image/png,image/jpeg"
        items={items}
        onAdd={(files) => void add(files)}
        onRemove={(key) => setItems((list) => list.filter((i) => i.key !== key))}
      />
    </section>
  );
}

function Photo({ photo, meId, onChanged, onGone }: { photo: PhotoView; meId: string; onChanged: (view: PhotoView) => void; onGone: (id: string) => void }) {
  const t = useTranslations("engagement.gallery");
  const [busy, setBusy] = useState<string | null>(null);
  const [messages, setMessages] = useState<string[]>([]);
  const [tagging, setTagging] = useState(false);
  const [tags, setTags] = useState<EngagementPerson[]>(photo.tags);
  const alt = photo.caption ?? t("photoBy", { name: photo.uploader.displayName });

  async function act(name: string, request: () => Promise<Response>, done: (response: Response) => Promise<void>) {
    setBusy(name);
    setMessages([]);
    const result = await call<Response>(async () => {
      const response = await request();
      if (response.ok) await done(response);
      return new Response(null, { status: response.ok ? 204 : response.status, headers: response.headers, statusText: response.statusText });
    }, t("failed"));
    if (!result.ok) setMessages(result.messages);
    setBusy(null);
  }

  return (
    <article className="engagement-post" aria-label={alt}>
      <Media src={photoFile(photo.id)} ratio="4:3" alt={alt} caption={photo.caption ?? undefined} />
      <p className="engagement-meta">
        <bdi>{photo.uploader.displayName}</bdi>
      </p>
      {photo.status === "pending" && photo.heldReason ? <Badge tone="warning">{t(`held.${photo.heldReason}`)}</Badge> : null}
      {photo.tags.length > 0 ? (
        <p className="engagement-meta">
          {t("inPhoto")} {photo.tags.map((p, i) => (
            <span key={p.id}>
              {i > 0 ? ", " : null}
              <bdi>{p.displayName}</bdi>
            </span>
          ))}
        </p>
      ) : null}
      <div className="engagement-row">
        {photo.status === "pending" && photo.canModerate ? (
          <Button size="sm" variant="primary" icon="check" busy={busy === "approve"} busyLabel={t("working")}
            onClick={() => void act("approve", () => send(`${engagementApi}/photos/${photo.id}:approve`, "POST"), async (r) => onChanged((await r.json()) as PhotoView))}>
            {t("approve")}
          </Button>
        ) : null}
        {photo.canManage && photo.status === "visible" ? (
          <Button size="sm" variant="secondary" icon="people" onClick={() => setTagging((v) => !v)}>
            {t("tagPeople")}
          </Button>
        ) : null}
        {photo.taggedMe ? (
          <Button size="sm" variant="quiet" busy={busy === "untag"} busyLabel={t("working")}
            onClick={() => void act("untag", () => send(`${engagementApi}/photos/${photo.id}/tags/me`, "DELETE"), async () => onChanged({ ...photo, taggedMe: false, tags: photo.tags.filter((p) => p.id !== meId) }))}>
            {t("removeMyTag")}
          </Button>
        ) : null}
        {photo.canManage || photo.canModerate ? (
          <Button size="sm" variant="quiet" icon="trash" busy={busy === "remove"} busyLabel={t("working")}
            onClick={() => void act("remove", () => send(`${engagementApi}/photos/${photo.id}`, "DELETE"), async () => onGone(photo.id))}>
            {t("remove")}
          </Button>
        ) : null}
      </div>
      {tagging ? (
        <form className="engagement" aria-label={t("tagPeople")}
          onSubmit={(e) => {
            e.preventDefault();
            void act("tags", () => send(`${engagementApi}/photos/${photo.id}/tags`, "PUT", { userIds: tags.map((p) => p.id) }), async (r) => {
              onChanged((await r.json()) as PhotoView);
              setTagging(false);
            });
          }}>
          <MentionPicker value={tags} onChange={setTags} />
          <div className="engagement-row">
            <Button type="submit" size="sm" variant="primary" busy={busy === "tags"} busyLabel={t("working")}>
              {t("saveTags")}
            </Button>
          </div>
        </form>
      ) : null}
      {messages.length > 0 ? (
        <Alert tone="danger" title={t("failed")} live>
          {messages.join(" ")}
        </Alert>
      ) : null}
    </article>
  );
}
