"use client";

import { useState, useSyncExternalStore, type FormEvent } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import { FormField } from "@/components/ui/FormField";
import { useMessages } from "@/components/ui/Messages";
import { SelectionBar } from "@/components/ui/SelectionBar";
import { Skeleton } from "@/components/ui/Skeleton";
import { StateView, type StateKind } from "@/components/ui/StateView";

const kinds: StateKind[] = ["loading", "success", "empty", "error", "denied", "offline", "unsupported"];
const items = ["one", "two", "three"] as const;
const noSubscription = () => () => {};

export function Gallery() {
  const t = useTranslations("gallery");
  const { show } = useMessages();
  const [nameError, setNameError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [draft, setDraft] = useState<"present" | "deleted">("present");
  const [selected, setSelected] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const hydrated = useSyncExternalStore(noSubscription, () => true, () => false);

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const name = String(new FormData(event.currentTarget).get("name") ?? "").trim();
    if (name.length < 3) {
      setNameError(t("form.error"));
      setSaved(false);
      return;
    }
    setNameError(null);
    setBusy(true);
    setTimeout(() => {
      setBusy(false);
      setSaved(true);
    }, 300);
  }

  return (
    <div className="gallery" data-hydrated={hydrated}>
      <section aria-labelledby="g-states">
        <h2 id="g-states">{t("states")}</h2>
        <div className="gallery-grid">
          {kinds.map((kind) => (
            <StateView key={kind} kind={kind} action={kind === "error" ? <Button>{t("retry")}</Button> : undefined} />
          ))}
        </div>
      </section>

      <section aria-labelledby="g-form" data-context="form">
        <h2 id="g-form">{t("form.title")}</h2>
        <form onSubmit={submit} noValidate>
          <FormField label={t("form.name")} name="name" hint={t("form.hint")} error={nameError} autoComplete="off" />
          <div className="ui-actions">
            <Button type="submit" variant="primary" busy={busy} busyLabel={t("form.saving")}>
              {t("form.save")}
            </Button>
            <Button type="reset" variant="secondary" onClick={() => setNameError(null)}>
              {t("form.cancel")}
            </Button>
          </div>
          <p role="status" className="notice">
            {saved ? t("form.saved") : ""}
          </p>
        </form>
      </section>

      <section aria-labelledby="g-confirm" data-context="confirm">
        <h2 id="g-confirm">{t("confirm.title")}</h2>
        <p data-testid="draft-state">{draft === "present" ? t("confirm.present") : t("confirm.deleted")}</p>
        {draft === "present" ? (
          <ConfirmAction
            label={t("confirm.delete")}
            question={t("confirm.question")}
            confirmLabel={t("confirm.yes")}
            cancelLabel={t("confirm.no")}
            onConfirm={() => {
              setDraft("deleted");
              show({ priority: "normal", text: t("confirm.done"), action: { label: t("confirm.undo"), run: () => setDraft("present") } });
            }}
          />
        ) : null}
      </section>

      <section aria-labelledby="g-messages" data-context="messages">
        <h2 id="g-messages">{t("messages.title")}</h2>
        <div className="ui-actions">
          <Button variant="secondary" onClick={() => show({ priority: "critical", text: t("messages.critical") })}>
            {t("messages.showCritical")}
          </Button>
          <Button variant="secondary" onClick={() => show({ priority: "normal", text: t("messages.normal") })}>
            {t("messages.showNormal")}
          </Button>
          <Button variant="secondary" onClick={() => show({ priority: "low", text: t("messages.low") })}>
            {t("messages.showLow")}
          </Button>
        </div>
      </section>

      <section aria-labelledby="g-skeleton">
        <h2 id="g-skeleton">{t("skeleton")}</h2>
        <Skeleton lines={["1.5rem", "1rem", "1rem", "6rem"]} />
      </section>

      <section aria-labelledby="g-selection" data-context="selection">
        <h2 id="g-selection">{t("selection.title")}</h2>
        <ul className="gallery-list">
          {items.map((item) => (
            <li key={item}>
              <label className="inline-field">
                <input
                  type="checkbox"
                  checked={selected.includes(item)}
                  onChange={(e) => setSelected(e.target.checked ? [...selected, item] : selected.filter((s) => s !== item))}
                />
                <span>{t(`selection.items.${item}`)}</span>
              </label>
            </li>
          ))}
        </ul>
        {selected.length > 0 ? (
          <SelectionBar
            summary={t("selection.summary", { count: selected.length })}
            consequence={t("selection.consequence")}
            cancelLabel={t("selection.cancel")}
            onCancel={() => setSelected([])}
          >
            <Button variant="primary">{t("selection.archive")}</Button>
          </SelectionBar>
        ) : null}
      </section>
    </div>
  );
}
