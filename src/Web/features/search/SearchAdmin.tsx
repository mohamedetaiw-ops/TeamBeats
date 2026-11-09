"use client";

import { useEffect, useId, useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { problemMessages } from "@/lib/notifications/types";
import type { BestBet, Rebuild, Suggestion, SynonymSet } from "@/lib/search/types";

type Message = { error: boolean; text: string } | null;

function Status({ message }: { message: Message }) {
  return (
    <div aria-live="polite">
      {message ? (
        <p className={message.error ? "field-error" : "notice"} role={message.error ? "alert" : "status"}>
          {message.text}
        </p>
      ) : null}
    </div>
  );
}

/** Sends a change and refreshes the page data; returns the problem text when the API refuses it. */
async function send(url: string, method: string, body?: unknown): Promise<string | null> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return response.ok ? null : (await problemMessages(response)).join(" ") || String(response.status);
}

function LocaleSelect({ id, value, onChange }: { id: string; value: string; onChange: (value: string) => void }) {
  const t = useTranslations("search.admin");
  return (
    <div className="field">
      <label htmlFor={id}>{t("locale")}</label>
      <select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{t("locales.any")}</option>
        <option value="ar">{t("locales.ar")}</option>
        <option value="en">{t("locales.en")}</option>
      </select>
    </div>
  );
}

/** Best bets (spec 012 US4 scenario 1): the words, an item picked from search, and the list with remove. */
export function BestBets({ initial }: { initial: BestBet[] }) {
  const t = useTranslations("search.admin");
  const tk = useTranslations("search.kinds");
  const router = useRouter();
  const [query, setQuery] = useState("");
  const [locale, setLocale] = useState("");
  const [find, setFind] = useState("");
  const [picks, setPicks] = useState<{ term: string; items: Suggestion[] } | null>(null);
  const [target, setTarget] = useState<Suggestion | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message>(null);
  const ids = { query: useId(), locale: useId(), find: useId() };
  const term = find.trim();
  const shown = term.length >= 2 && picks?.term === term ? picks.items : [];

  useEffect(() => {
    if (term.length < 2) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      fetch(`/api/v1/search/suggest?q=${encodeURIComponent(term)}`, { signal: controller.signal })
        .then((r) => (r.ok ? (r.json() as Promise<Suggestion[]>) : []))
        .then((items) => setPicks({ term, items }))
        .catch(() => undefined);
    }, 200);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [term]);

  async function add(e: FormEvent) {
    e.preventDefault();
    if (!target) {
      setMessage({ error: true, text: t("bestBets.pickFirst") });
      return;
    }
    setBusy(true);
    setMessage(null);
    try {
      const problem = await send("/api/v1/search/best-bets", "POST", { query, locale: locale || null, source: target.source, itemId: target.itemId });
      if (problem) {
        setMessage({ error: true, text: problem });
        return;
      }
      setQuery("");
      setFind("");
      setTarget(null);
      setMessage({ error: false, text: t("bestBets.added") });
      router.refresh();
    } catch {
      setMessage({ error: true, text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  async function remove(bet: BestBet) {
    setMessage(null);
    const problem = await send(`/api/v1/search/best-bets/${encodeURIComponent(bet.id)}`, "DELETE").catch(() => t("failed"));
    setMessage(problem ? { error: true, text: problem } : { error: false, text: t("bestBets.removed") });
    router.refresh();
  }

  return (
    <section aria-labelledby="best-bets-title">
      <h2 id="best-bets-title">{t("bestBets.title")}</h2>
      <p>{t("bestBets.intro")}</p>
      <form className="search-admin-form" onSubmit={(e) => void add(e)}>
        <div className="field">
          <label htmlFor={ids.query}>{t("bestBets.query")}</label>
          <input id={ids.query} value={query} maxLength={200} required onChange={(e) => setQuery(e.target.value)} />
        </div>
        <LocaleSelect id={ids.locale} value={locale} onChange={setLocale} />
        <div className="field">
          <label htmlFor={ids.find}>{t("bestBets.find")}</label>
          <input id={ids.find} type="search" value={find} maxLength={200} onChange={(e) => setFind(e.target.value)} />
        </div>
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
          {t("bestBets.add")}
        </Button>
      </form>
      {target ? (
        <p role="status">
          {t("bestBets.picked")} <bdi>{target.title}</bdi> ({tk(target.kind)})
        </p>
      ) : null}
      {shown.length > 0 ? (
        <ul className="search-picks" aria-label={t("bestBets.results")}>
          {shown.map((s) => (
            <li key={`${s.source}:${s.itemId}`}>
              <Button variant="quiet" aria-pressed={target?.itemId === s.itemId} onClick={() => setTarget(s)}>
                <bdi>{s.title}</bdi> ({tk(s.kind)})
              </Button>
            </li>
          ))}
        </ul>
      ) : null}
      <Status message={message} />
      {initial.length === 0 ? (
        <p>{t("bestBets.empty")}</p>
      ) : (
        <ul className="search-admin-list">
          {initial.map((bet) => (
            <li key={bet.id}>
              <span>
                <bdi>{bet.query}</bdi>
                {" · "}
                {bet.title ? <bdi>{bet.title}</bdi> : t("bestBets.gone")}
                {" · "}
                {bet.locale ? t(`locales.${bet.locale}`) : t("locales.any")}
              </span>
              <Button variant="destructive" aria-label={t("bestBets.remove", { query: bet.query })} onClick={() => void remove(bet)}>
                {t("remove")}
              </Button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Synonym sets (spec 012 US4 scenario 2): words separated by commas, a language, and the list with remove. */
export function Synonyms({ initial }: { initial: SynonymSet[] }) {
  const t = useTranslations("search.admin");
  const router = useRouter();
  const [words, setWords] = useState("");
  const [locale, setLocale] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message>(null);
  const ids = { words: useId(), locale: useId() };

  async function add(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      const terms = words.split(/[,،]/).map((w) => w.trim()).filter(Boolean);
      const problem = await send("/api/v1/search/synonyms", "POST", { locale: locale || null, terms });
      if (problem) {
        setMessage({ error: true, text: problem });
        return;
      }
      setWords("");
      setMessage({ error: false, text: t("synonyms.added") });
      router.refresh();
    } catch {
      setMessage({ error: true, text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  async function remove(set: SynonymSet) {
    setMessage(null);
    const problem = await send(`/api/v1/search/synonyms/${encodeURIComponent(set.id)}`, "DELETE").catch(() => t("failed"));
    setMessage(problem ? { error: true, text: problem } : { error: false, text: t("synonyms.removed") });
    router.refresh();
  }

  return (
    <section aria-labelledby="synonyms-title">
      <h2 id="synonyms-title">{t("synonyms.title")}</h2>
      <p>{t("synonyms.intro")}</p>
      <form className="search-admin-form" onSubmit={(e) => void add(e)}>
        <div className="field">
          <label htmlFor={ids.words}>{t("synonyms.words")}</label>
          <p id={`${ids.words}-hint`} className="field-hint">
            {t("synonyms.hint")}
          </p>
          <input id={ids.words} value={words} required aria-describedby={`${ids.words}-hint`} onChange={(e) => setWords(e.target.value)} />
        </div>
        <LocaleSelect id={ids.locale} value={locale} onChange={setLocale} />
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
          {t("synonyms.add")}
        </Button>
      </form>
      <Status message={message} />
      {initial.length === 0 ? (
        <p>{t("synonyms.empty")}</p>
      ) : (
        <ul className="search-admin-list">
          {initial.map((set) => (
            <li key={set.id}>
              <span>
                <bdi>{set.terms.join(", ")}</bdi>
                {" · "}
                {set.locale ? t(`locales.${set.locale}`) : t("locales.any")}
              </span>
              <Button variant="destructive" aria-label={t("synonyms.remove", { words: set.terms.join(", ") })} onClick={() => void remove(set)}>
                {t("remove")}
              </Button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Index rebuilds (spec 012 FR-012): start one and see the recent ones. */
export function Rebuilds({ initial }: { initial: Rebuild[] }) {
  const t = useTranslations("search.admin");
  const format = useFormatter();
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<Message>(null);

  async function start() {
    setBusy(true);
    setMessage(null);
    try {
      const problem = await send("/api/v1/search/rebuilds", "POST");
      setMessage(problem ? { error: true, text: problem } : { error: false, text: t("rebuilds.started") });
      router.refresh();
    } catch {
      setMessage({ error: true, text: t("failed") });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="rebuilds-title">
      <h2 id="rebuilds-title">{t("rebuilds.title")}</h2>
      <p>{t("rebuilds.intro")}</p>
      <div className="button-row">
        <Button busy={busy} busyLabel={t("busy")} onClick={() => void start()}>
          {t("rebuilds.start")}
        </Button>
      </div>
      <Status message={message} />
      {initial.length === 0 ? (
        <p>{t("rebuilds.empty")}</p>
      ) : (
        <ul className="search-admin-list">
          {initial.map((r) => (
            <li key={r.id}>
              <span>{format.dateTime(new Date(r.requestedAt), { dateStyle: "medium", timeStyle: "short" })}</span>
              <span>{r.completedAt ? t("rebuilds.done", { items: r.items, units: r.units }) : r.startedAt ? t("rebuilds.running") : t("rebuilds.queued")}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
