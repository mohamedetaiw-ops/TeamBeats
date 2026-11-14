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
import { Icon } from "@/components/ui/Icon";
import { StateView } from "@/components/ui/StateView";
import "./documents.css";

type Answer = "loading" | "failed" | ConnectedPage;

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
  const base = `/api/v1/documents/libraries/${encodeURIComponent(library.id)}/connected/items`;

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
  } else if (answer.state !== "ok") {
    body = <StateView kind="empty" icon="folder" title={t("notFound")} message="" />;
  } else if (answer.items.length === 0) {
    body = <StateView kind="empty" icon="folder" title={t("empty")} message="" />;
  } else {
    body = (
      <>
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
                    {item.folder ? (
                      <Link className="documents-name" href={trailHref(path, [...trail, { id: item.id, name: item.name }])}>
                        <Icon name="folder" size={18} />
                        <bdi>{item.name}</bdi>
                      </Link>
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
