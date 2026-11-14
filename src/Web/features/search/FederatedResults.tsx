"use client";

import { useEffect, useState } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { connectHref } from "@/components/experience/outlook";
import { Skeleton } from "@/components/ui/Skeleton";
import { StateView } from "@/components/ui/StateView";
import type { FederatedGroup, FederatedPage } from "@/lib/search/types";
import "./search.css";

/**
 * Federated results (spec 012 US5): asked for after the TeamBeats results are on the page, so a slow source never holds
 * them up. Each source the tenant turned on is its own group, searched live as the person, so the external system's
 * permissions decide; the API cuts a source off at 3 seconds and the group then says it is not available now. Nothing
 * shows when federated search is not in the package or no source answers for this person.
 */
export function FederatedResults({ q }: { q: string }) {
  const t = useTranslations("search.federated");
  const format = useFormatter();
  const [groups, setGroups] = useState<FederatedGroup[] | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const response = await fetch(
          `/api/v1/search/federated?${new URLSearchParams({ q })}`,
        );
        const page = response.ok
          ? ((await response.json()) as FederatedPage)
          : null;
        if (live) setGroups(page?.groups ?? []);
      } catch {
        if (live) setGroups([]);
      }
    })();
    return () => {
      live = false;
    };
  }, [q]);

  if (groups === null) {
    return (
      <section
        className="search-federated"
        aria-labelledby="federated-loading"
        aria-busy="true"
      >
        <h2 id="federated-loading" className="visually-hidden">
          {t("loading")}
        </h2>
        <Skeleton lines={["1.5rem", "4rem", "4rem"]} />
      </section>
    );
  }
  const returnTo = `/search?${new URLSearchParams({ q })}`;
  return (
    <>
      {groups.map((g) => (
        <section
          key={g.source}
          className="search-federated"
          aria-labelledby={`federated-${g.source}`}
          data-testid={`federated-${g.source}`}
        >
          <h2 id={`federated-${g.source}`}>{t(`source.${g.source}`)}</h2>
          <p className="search-count">{t("live")}</p>
          {g.state === "not_connected" || g.state === "reconnect" ? (
            <StateView
              kind="denied"
              title={t(g.state === "reconnect" ? "reconnect" : "connect")}
              message={t("connectHint")}
              action={
                <a
                  className="button button-primary"
                  href={connectHref(returnTo)}
                >
                  {t("connectAction")}
                </a>
              }
            />
          ) : g.state !== "ok" ? (
            <StateView
              kind="offline"
              title={t("notNow")}
              message={t(g.state === "timeout" ? "timeout" : "unavailable")}
            />
          ) : g.results.length === 0 ? (
            <StateView
              kind="empty"
              icon="search"
              title={t("none")}
              message=""
            />
          ) : (
            <ol className="search-results">
              {g.results.map((r) => (
                <li key={r.link} className="search-result">
                  <div className="search-result-meta">
                    <span className="search-kind">
                      {t(`source.${g.source}`)}
                    </span>
                    {r.location ? <bdi>{r.location}</bdi> : null}
                    {r.modifiedAt ? (
                      <time dateTime={r.modifiedAt}>
                        {format.dateTime(new Date(r.modifiedAt), {
                          dateStyle: "medium",
                        })}
                      </time>
                    ) : null}
                  </div>
                  <h3 className="search-result-title">
                    <a href={r.link} target="_blank" rel="noopener noreferrer">
                      <bdi>{r.title}</bdi>
                      <span className="visually-hidden">
                        {" "}
                        {t("opensOutside")}
                      </span>
                    </a>
                  </h3>
                  {r.summary ? (
                    <p className="search-snippet">{r.summary}</p>
                  ) : null}
                </li>
              ))}
            </ol>
          )}
        </section>
      ))}
    </>
  );
}
