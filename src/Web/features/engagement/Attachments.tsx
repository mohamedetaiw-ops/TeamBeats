"use client";

import { useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FileUpload, type UploadItem } from "@/components/ui/FileUpload";
import { Icon } from "@/components/ui/Icon";
import { Media } from "@/components/ui/Media";
import { problemMessages } from "@/lib/notifications/types";
import { attachmentFile, engagementApi, type AttachmentView } from "@/lib/engagement";
import "./engagement.css";

const accept = [
  "image/png",
  "image/jpeg",
  "application/pdf",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  "text/plain",
  "text/csv",
].join(",");

/**
 * Adding images and files to a post (spec 023 FR-003). Each file is sent on its own; the API scans it, cleans images
 * of their metadata and checks them for content safety, and says in words why one was refused. The chosen ones are
 * joined to the post when it is saved.
 */
export function AttachmentPicker({ communityId, value, onChange }: { communityId: string; value: AttachmentView[]; onChange: (next: AttachmentView[]) => void }) {
  const t = useTranslations("engagement.attachments");
  const [items, setItems] = useState<UploadItem[]>([]);

  async function add(files: File[]) {
    let next = value;
    for (const file of files) {
      const key = `${file.name}-${file.size}-${file.lastModified}`;
      setItems((list) => [...list.filter((i) => i.key !== key), { key, name: file.name, size: file.size, progress: 0 }]);
      let error: string | undefined;
      try {
        const response = await fetch(`${engagementApi}/communities/${communityId}/attachments?name=${encodeURIComponent(file.name)}`, {
          method: "POST",
          headers: { "content-type": file.type || "application/octet-stream" },
          body: file,
        });
        if (response.ok) {
          next = [...next, (await response.json()) as AttachmentView];
          onChange(next);
        } else {
          error = (await problemMessages(response)).join(" ");
        }
      } catch {
        error = t("failed");
      }
      setItems((list) => (error ? list.map((i) => (i.key === key ? { ...i, progress: undefined, error } : i)) : list.filter((i) => i.key !== key)));
    }
  }

  return (
    <div className="engagement">
      <FileUpload
        label={t("add")}
        hint={t("hint")}
        accept={accept}
        multiple
        items={items}
        onAdd={(files) => void add(files)}
        onRemove={(key) => setItems((list) => list.filter((i) => i.key !== key))}
      />
      {value.length > 0 ? (
        <ul className="engagement-tags" aria-label={t("chosen")}>
          {value.map((a) => (
            <li key={a.id}>
              <Button size="xs" variant="quiet" icon="close" aria-label={t("remove", { name: a.name })} onClick={() => onChange(value.filter((x) => x.id !== a.id))}>
                <bdi>{a.name}</bdi>
              </Button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

/** The images and files of a post: images in a grid with their file name as the description, files as download links. */
export function PostAttachmentList({ attachments }: { attachments: AttachmentView[] | undefined }) {
  const t = useTranslations("engagement.attachments");
  const format = useFormatter();
  if (!attachments || attachments.length === 0) return null;
  const images = attachments.filter((a) => a.kind === "image");
  const files = attachments.filter((a) => a.kind === "file");
  return (
    <div className="engagement">
      {images.length > 0 ? (
        <ul className="engagement-grid" aria-label={t("images")}>
          {images.map((a) => (
            <li key={a.id}>
              <Media src={attachmentFile(a.id)} ratio="4:3" alt={t("imageAlt", { name: a.name })} />
            </li>
          ))}
        </ul>
      ) : null}
      {files.length > 0 ? (
        <ul className="engagement-tags" aria-label={t("files")}>
          {files.map((a) => (
            <li key={a.id}>
              <a href={attachmentFile(a.id)} download={a.name}>
                <Icon name="attach" size={16} /> <bdi>{a.name}</bdi>{" "}
                <span className="engagement-meta">{t("size", { size: format.number(Math.max(1, Math.round(a.bytes / 1024))) })}</span>
              </a>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
