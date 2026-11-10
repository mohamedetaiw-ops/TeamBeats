"use client";

import { useId, useState, type DragEvent } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Icon } from "./Icon";
import { Progress } from "./Status";

export type UploadItem = {
  key: string;
  name: string;
  /** Size in bytes. */
  size: number;
  /** 0 to 100 while the file is being sent; leave out when it is done or has not started. */
  progress?: number;
  /** Why the file was refused or failed, in words from the API. */
  error?: string;
};

/**
 * File upload (spec 036 DLS-035): a drop area that is also a button, and the list of chosen files with their progress
 * and errors. The area is the label of a native file input, so it opens the file chooser with Enter or Space and works
 * without drag and drop. The component only collects files and shows state: sending them, and which types and sizes
 * are allowed, belong to the feature and the API (`accept` is a convenience for the chooser, not a check).
 */
export function FileUpload({
  label,
  hint,
  accept,
  multiple = true,
  items,
  onAdd,
  onRemove,
  error,
}: {
  label: string;
  /** What may be uploaded, in words: "PDF or Word, up to 20 MB each". */
  hint?: string;
  accept?: string;
  multiple?: boolean;
  items: readonly UploadItem[];
  onAdd: (files: File[]) => void;
  onRemove: (key: string) => void;
  error?: string | null;
}) {
  const t = useTranslations("ui.controls");
  const locale = useLocale();
  const id = useId();
  const [over, setOver] = useState(false);
  const size = new Intl.NumberFormat(locale, { style: "unit", unit: "megabyte", maximumFractionDigits: 1 });
  const describedBy = [hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;

  function onDrop(event: DragEvent<HTMLLabelElement>) {
    event.preventDefault();
    setOver(false);
    const files = [...event.dataTransfer.files];
    if (files.length > 0) onAdd(multiple ? files : files.slice(0, 1));
  }

  return (
    <div className="ui-upload">
      <label
        htmlFor={id}
        className="ui-upload-area"
        data-over={over || undefined}
        onDragOver={(event) => {
          event.preventDefault();
          setOver(true);
        }}
        onDragLeave={() => setOver(false)}
        onDrop={onDrop}
      >
        <input
          id={id}
          type="file"
          className="ui-upload-input"
          accept={accept}
          multiple={multiple}
          aria-describedby={describedBy}
          aria-invalid={error ? true : undefined}
          onChange={(event) => {
            const files = [...(event.currentTarget.files ?? [])];
            // Cleared so that choosing the same file again is still a change.
            event.currentTarget.value = "";
            if (files.length > 0) onAdd(files);
          }}
        />
        <span className="ui-upload-icon" aria-hidden="true">
          <Icon name="upload" size={20} />
        </span>
        <span className="ui-upload-label">{label}</span>
        <span className="ui-upload-drop">{t("dropHere")}</span>
        {hint ? (
          <span id={`${id}-hint`} className="field-hint">
            {hint}
          </span>
        ) : null}
      </label>
      {error ? (
        <p id={`${id}-error`} className="field-error" role="alert">
          {error}
        </p>
      ) : null}
      {items.length > 0 ? (
        <ul className="ui-upload-list" aria-label={t("chosenFiles")}>
          {items.map((item) => (
            <li key={item.key} className="ui-upload-item" data-error={item.error ? true : undefined}>
              <span className="ui-upload-icon" aria-hidden="true">
                <Icon name={item.error ? "alert" : "files"} size={18} />
              </span>
              <span className="ui-upload-text">
                <bdi className="ui-upload-name">{item.name}</bdi>
                <span className="field-hint">{size.format(item.size / 1_048_576)}</span>
                {item.error ? (
                  <span className="field-error" role="alert">
                    {item.error}
                  </span>
                ) : null}
                {item.progress !== undefined && !item.error ? <Progress label={t("uploading", { name: item.name })} value={item.progress} max={100} /> : null}
              </span>
              <button type="button" className="ui-input-action" aria-label={t("remove", { name: item.name })} onClick={() => onRemove(item.key)}>
                <Icon name="close" size={16} />
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
