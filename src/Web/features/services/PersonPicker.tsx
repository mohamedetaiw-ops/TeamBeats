"use client";

import { useEffect, useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import type { PersonCard } from "@/lib/people/types";
import { personName } from "@/lib/people/types";

/**
 * Picks one colleague through the people directory search (spec 010), so a form only ever holds a person ID the API
 * returned. Read-only mode shows the chosen person's name.
 */
export function PersonPicker({
  label,
  value,
  onChange,
  readOnly = false,
  error,
}: {
  label: string;
  value: string | null;
  onChange: (id: string | null) => void;
  readOnly?: boolean;
  error?: string | null;
}) {
  const t = useTranslations("services.person");
  const locale = useLocale();
  const id = useId();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<PersonCard[] | null>(null);
  const [chosen, setChosen] = useState<PersonCard | null>(null);

  useEffect(() => {
    if (!value || chosen?.id === value) return;
    let cancelled = false;
    void (async () => {
      try {
        const response = await fetch(`/api/v1/people?ids=${encodeURIComponent(value)}`, { cache: "no-store" });
        if (response.ok && !cancelled) {
          const page = (await response.json()) as { items: PersonCard[] };
          setChosen(page.items[0] ?? null);
        }
      } catch {
        // The ID stays; only the name is missing.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [value, chosen]);

  async function search() {
    if (!query.trim()) return;
    try {
      const response = await fetch(`/api/v1/people?q=${encodeURIComponent(query.trim())}&limit=8`, { cache: "no-store" });
      setResults(response.ok ? ((await response.json()) as { items: PersonCard[] }).items : []);
    } catch {
      setResults([]);
    }
  }

  const name = chosen && chosen.id === value ? personName(chosen, locale) : null;
  if (readOnly) {
    return (
      <div className="field">
        <span id={`${id}-label`}>{label}</span>
        <p aria-labelledby={`${id}-label`}>
          <bdi>{name ?? t("none")}</bdi>
        </p>
      </div>
    );
  }
  return (
    <fieldset aria-describedby={error ? `${id}-error` : undefined}>
      <legend>{label}</legend>
      <p className="services-meta" aria-live="polite">
        {value ? t("chosen", { name: name ?? "" }) : t("none")}
      </p>
      <div className="services-toolbar">
        <label className="services-select">
          {t("search")}
          <input type="search" value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
        <Button onClick={() => void search()}>{t("find")}</Button>
        {value ? (
          <Button variant="quiet" onClick={() => onChange(null)}>
            {t("clear")}
          </Button>
        ) : null}
      </div>
      {results ? (
        results.length === 0 ? (
          <p className="services-meta">{t("noMatch")}</p>
        ) : (
          <ul className="services-list" aria-label={t("results")}>
            {results.map((p) => (
              <li key={p.id}>
                <Button
                  variant="quiet"
                  onClick={() => {
                    setChosen(p);
                    setResults(null);
                    onChange(p.id);
                  }}
                >
                  <bdi>{personName(p, locale)}</bdi>
                </Button>
              </li>
            ))}
          </ul>
        )
      ) : null}
      {error ? (
        <p id={`${id}-error`} className="field-error" role="alert">
          {error}
        </p>
      ) : null}
    </fieldset>
  );
}
