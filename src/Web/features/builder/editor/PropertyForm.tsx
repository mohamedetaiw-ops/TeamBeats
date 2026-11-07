"use client";

import { useId } from "react";
import { useLocale, useTranslations } from "next-intl";
import type { PropertySchema } from "./model";

type Values = Record<string, unknown>;

/**
 * Property inputs drawn from the manifest's typed schema alone (spec 032 FR-025): no component has builder code of its
 * own. Localized text gets one input per enabled locale, each with its language and direction. The API validates every
 * value again on save.
 */
export function PropertyForm({
  schema,
  labels,
  values,
  locales,
  onChange,
  problems,
}: {
  schema: Record<string, PropertySchema>;
  labels: Record<string, string>;
  values: Values;
  locales: string[];
  onChange: (values: Values) => void;
  problems: Record<string, string>;
}) {
  const t = useTranslations("builder.properties");
  const base = useId();
  const languages = new Intl.DisplayNames([useLocale()], { type: "language" });
  const names = Object.keys(schema);
  if (names.length === 0) return <p className="notice">{t("none")}</p>;

  const set = (name: string, value: unknown) => {
    const next = { ...values };
    if (value === undefined || value === "" || value === null) delete next[name];
    else next[name] = value;
    onChange(next);
  };

  return (
    <div className="builder-properties">
      {names.map((name) => {
        const s = schema[name]!;
        const id = `${base}-${name}`;
        const label = labels[name] ?? name;
        const problem = problems[name];
        const errorId = problem ? `${id}-error` : undefined;
        const required = s.required ? <span className="builder-required">{t("required")}</span> : null;
        const error = problem ? (
          <p id={errorId} className="field-error">
            {problem}
          </p>
        ) : null;
        const value = values[name];

        switch (s.type) {
          case "text": {
            const text = (value && typeof value === "object" ? value : {}) as Record<string, string>;
            return (
              <fieldset key={name} className="builder-text" aria-describedby={errorId}>
                <legend>
                  {label} {required}
                </legend>
                {locales.map((locale) => {
                  const inputId = `${id}-${locale}`;
                  const long = (s.maxLength ?? 0) > 200;
                  const common = {
                    id: inputId,
                    lang: locale,
                    dir: locale === "ar" ? "rtl" : "ltr",
                    value: text[locale] ?? "",
                    maxLength: s.maxLength ?? undefined,
                    "aria-invalid": problem ? true : undefined,
                    onChange: (e: { target: { value: string } }) => {
                      const next = { ...text, [locale]: e.target.value };
                      if (!e.target.value) delete next[locale];
                      set(name, Object.keys(next).length ? next : undefined);
                    },
                  } as const;
                  return (
                    <div key={locale} className="field">
                      <label htmlFor={inputId}>{t("inLanguage", { language: languages.of(locale) ?? locale })}</label>
                      {long ? <textarea rows={4} {...common} /> : <input type="text" {...common} />}
                    </div>
                  );
                })}
                {error}
              </fieldset>
            );
          }
          case "boolean":
            return (
              <div key={name} className="field builder-check">
                <input id={id} type="checkbox" checked={value === true} onChange={(e) => set(name, e.target.checked)} aria-describedby={errorId} />
                <label htmlFor={id}>{label}</label>
                {error}
              </div>
            );
          case "enum":
            return (
              <div key={name} className="field">
                <label htmlFor={id}>
                  {label} {required}
                </label>
                <select id={id} value={typeof value === "string" ? value : ""} onChange={(e) => set(name, e.target.value)} aria-describedby={errorId}>
                  {s.required ? null : <option value="">{t("notSet")}</option>}
                  {(s.enum ?? []).map((option) => (
                    <option key={option} value={option}>
                      {labels[`${name}.${option}`] ?? option}
                    </option>
                  ))}
                </select>
                {error}
              </div>
            );
          case "number":
          case "integer":
            return (
              <div key={name} className="field">
                <label htmlFor={id}>
                  {label} {required}
                </label>
                <input
                  id={id}
                  type="number"
                  inputMode={s.type === "integer" ? "numeric" : "decimal"}
                  step={s.type === "integer" ? 1 : "any"}
                  min={s.minimum ?? undefined}
                  max={s.maximum ?? undefined}
                  value={typeof value === "number" ? value : ""}
                  aria-invalid={problem ? true : undefined}
                  aria-describedby={errorId}
                  onChange={(e) => set(name, e.target.value === "" ? undefined : Number(e.target.value))}
                />
                {error}
              </div>
            );
          case "list": {
            const items = Array.isArray(value) ? (value as string[]) : [];
            const max = s.maxItems ?? 20;
            return (
              <fieldset key={name} className="builder-list" aria-describedby={errorId}>
                <legend>
                  {label} {required}
                </legend>
                {items.map((item, index) => (
                  <div key={index} className="builder-list-item">
                    <label className="visually-hidden" htmlFor={`${id}-${index}`}>
                      {t("item", { label, number: String(index + 1) })}
                    </label>
                    <input
                      id={`${id}-${index}`}
                      type={s.items?.type === "url" ? "url" : "text"}
                      dir="ltr"
                      value={item}
                      onChange={(e) => set(name, items.map((x, i) => (i === index ? e.target.value : x)))}
                    />
                    <button type="button" className="button button-quiet" onClick={() => set(name, items.filter((_, i) => i !== index))}>
                      {t("removeItem", { number: String(index + 1) })}
                    </button>
                  </div>
                ))}
                {items.length < max ? (
                  <button type="button" className="button button-secondary" onClick={() => set(name, [...items, ""])}>
                    {t("addItem")}
                  </button>
                ) : null}
                {error}
              </fieldset>
            );
          }
          default: {
            // string, url, image (an asset reference) and color_token are single-line text the API checks.
            const hint = s.type === "url" ? t("hints.url") : s.type === "image" ? t("hints.image") : s.type === "color_token" ? t("hints.colorToken") : null;
            const hintId = hint ? `${id}-hint` : undefined;
            return (
              <div key={name} className="field">
                <label htmlFor={id}>
                  {label} {required}
                </label>
                {hint ? (
                  <p id={hintId} className="field-hint">
                    {hint}
                  </p>
                ) : null}
                <input
                  id={id}
                  type={s.type === "url" ? "url" : "text"}
                  dir={s.type === "string" ? "auto" : "ltr"}
                  value={typeof value === "string" ? value : ""}
                  maxLength={s.maxLength ?? undefined}
                  aria-invalid={problem ? true : undefined}
                  aria-describedby={[hintId, errorId].filter(Boolean).join(" ") || undefined}
                  onChange={(e) => set(name, e.target.value)}
                />
                {error}
              </div>
            );
          }
        }
      })}
    </div>
  );
}
