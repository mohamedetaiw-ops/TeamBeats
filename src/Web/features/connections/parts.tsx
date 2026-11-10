"use client";

import { useTranslations } from "next-intl";
import { CONNECTION_STATUSES, ERROR_CLASSES, known } from "@/lib/integrations/types";

/** A connection status as words, never by color alone. */
export function StatusBadge({ status }: { status: string }) {
  const t = useTranslations("connections");
  return (
    <span className="connections-status" data-status={status}>
      {known(CONNECTION_STATUSES, status) ? t(`status.${status}`) : status}
    </span>
  );
}

/** What went wrong, from the runtime's error class (spec 017 section 6). */
export function ErrorText({ errorClass }: { errorClass: string | null | undefined }) {
  const t = useTranslations("connections");
  if (!errorClass) return null;
  return <>{known(ERROR_CLASSES, errorClass) ? t(`errors.${errorClass}`) : errorClass}</>;
}

export function Messages({ messages, ok = false }: { messages: string[]; ok?: boolean }) {
  if (messages.length === 0) return null;
  return (
    <div role={ok ? "status" : "alert"} className={ok ? "notice" : "notice notice-error"}>
      {messages.map((m) => (
        <p key={m}>{m}</p>
      ))}
    </div>
  );
}
