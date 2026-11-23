"use client";

import { useEffect, useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Accordion } from "@/components/ui/Accordion";
import { ConfirmAction } from "@/components/ui/ConfirmAction";
import type { DraftRevision, PageDefinition, PageView } from "@/lib/experience/builder-types";
import { useHydrated } from "@/lib/clock";
import { problemMessages } from "@/lib/notifications/types";
import { SendMessages, useSend } from "@/features/builder/manage/useSend";

function load(pageId: string): Promise<DraftRevision[]> {
  return fetch(`/api/v1/experience/pages/${pageId}/draft/revisions`, { cache: "no-store" })
    .then(async (r) => (r.ok ? ((await r.json()) as DraftRevision[]) : []))
    .catch(() => []);
}

/**
 * Kept draft revisions (spec 032 R2 T026, FR-043): the drafts that later saves replaced, newest first (the last 50). Restoring one saves it
 * as the draft again (a new revision), so nothing is lost; publishing still goes through validation and approval.
 */
export function DraftRevisions({ pageId }: { pageId: string }) {
  const t = useTranslations("builder.revisions");
  const format = useFormatter();
  const hydrated = useHydrated();
  const router = useRouter();
  const listId = useId();
  const [revisions, setRevisions] = useState<DraftRevision[] | null>(null);
  const { result, setResult } = useSend(t("failed"));

  useEffect(() => {
    let live = true;
    void load(pageId).then((list) => {
      if (live) setRevisions(list);
    });
    return () => {
      live = false;
    };
  }, [pageId]);

  async function restore(revision: number) {
    setResult(null);
    const [old, current] = await Promise.all([
      fetch(`/api/v1/experience/pages/${pageId}/draft/revisions/${revision}`, { cache: "no-store" }).catch(() => null),
      fetch(`/api/v1/experience/pages/${pageId}`, { cache: "no-store" }).catch(() => null),
    ]);
    if (!old?.ok || !current?.ok) {
      setResult({ ok: false, messages: [t("failed")] });
      return;
    }
    const definition = (await old.json()) as PageDefinition;
    const page = (await current.json()) as PageView;
    const response = await fetch(`/api/v1/experience/pages/${pageId}/draft`, {
      method: "PUT",
      headers: { "content-type": "application/json", "If-Match": page.eTag },
      body: JSON.stringify({ definition }),
    }).catch(() => null);
    if (response?.ok) {
      setResult({ ok: true, messages: [t("restored", { revision: String(revision) })] });
      // The editor opens the restored draft; the draft it replaced is now in this list too.
      setRevisions(await load(pageId));
      router.refresh();
      return;
    }
    // The API's answer (for example an edit made meanwhile) is shown as it is.
    const messages = response ? await problemMessages(response) : [];
    setResult({ ok: false, messages: messages.length > 0 ? messages : [t("failed")] });
  }

  if (revisions === null || revisions.length === 0) return null;
  return (
    <div className="builder-revisions" data-testid="draft-revisions">
      <Accordion
        items={[
          {
            key: listId,
            title: t("title", { count: revisions.length }),
            content: (
              <ol className="builder-manage-list">
                {revisions.map((r) => (
                  <li key={r.revision} className="builder-revision">
                    <span>
                      {t("line", {
                        revision: String(r.revision),
                        when: hydrated ? format.dateTime(new Date(r.updatedAt), { dateStyle: "medium", timeStyle: "short" }) : "",
                      })}
                    </span>
                    <ConfirmAction
                      label={t("restore")}
                      question={t("restoreQuestion", { revision: String(r.revision) })}
                      confirmLabel={t("restoreConfirm")}
                      cancelLabel={t("cancel")}
                      onConfirm={() => restore(r.revision)}
                    />
                  </li>
                ))}
              </ol>
            ),
          },
        ]}
      />
      <SendMessages result={result} />
    </div>
  );
}
