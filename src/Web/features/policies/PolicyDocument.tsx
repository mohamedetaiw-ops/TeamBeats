import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { BodyView } from "@/features/content/BodyView";
import { nameIn, shownLocale, versionHref, policyHref, type PolicyReading } from "@/lib/policies/types";
import "./policies.css";

/**
 * A policy version for a reader (spec 025 US1 scenarios 4 and 5): numbered clauses with stable anchors that links and
 * assistant citations point at, the note on the binding language when the reader sees another one, and on a superseded
 * version the banner that links to the version in force.
 */
export function PolicyDocument({ reading, locale }: { reading: PolicyReading; locale: string }) {
  const t = useTranslations("policies.reading");
  const format = useFormatter();
  const { policy, version } = reading;
  const shown = shownLocale(version, locale, reading.bindingLocale);
  const title = version.titles[shown] ?? nameIn(policy.names, locale);
  const others = version.locales.filter((l) => l !== shown);
  return (
    <article lang={shown} className="policies-document" data-testid="policy-document">
      <h1>
        <bdi>{title}</bdi>
      </h1>
      <p className="policies-meta">
        <span className="policies-badge">
          <bdi dir="ltr">{policy.code}</bdi>
        </span>
        {t("version", { number: version.number ?? 0 })}
        {version.effectiveFrom ? <> · {t("effective", { date: format.dateTime(new Date(version.effectiveFrom), { dateStyle: "long" }) })}</> : null}
        {" · "}
        {t("owner", { name: policy.owner.displayName })}
      </p>
      {reading.superseded ? (
        <div className="policies-banner" role="note" data-testid="superseded-banner">
          <p>
            {t("superseded", { number: version.number ?? 0 })}{" "}
            {reading.currentVersion ? (
              <Link href={versionHref(policy.id, reading.currentVersion)}>{t("readCurrent", { number: reading.currentVersion })}</Link>
            ) : null}
          </p>
        </div>
      ) : null}
      {reading.bindingLocale && reading.bindingLocale !== shown ? (
        <p className="policies-banner" role="note" data-testid="binding-note">
          {t("binding", { language: t(`language.${reading.bindingLocale}`) })}
        </p>
      ) : null}
      {others.length > 0 ? (
        <p className="policies-meta">
          {t("otherLanguages")}{" "}
          {others.map((l, i) => (
            <span key={l}>
              {i > 0 ? " · " : null}
              <Link href={`${reading.superseded ? versionHref(policy.id, version.number ?? 0) : policyHref(policy.id)}?lang=${l}`} hrefLang={l}>
                {t(`language.${l}`)}
              </Link>
            </span>
          ))}
        </p>
      ) : null}
      {version.clauses.length > 1 ? (
        <nav aria-label={t("contents")} className="policies-toc">
          <ol>
            {version.clauses.map((c) => (
              <li key={c.key} data-level={c.level}>
                <a href={`#${c.anchor}`}>
                  <span className="policies-number" dir="ltr">
                    {c.number}
                  </span>
                  <bdi>{c.titles[shown] ?? ""}</bdi>
                </a>
              </li>
            ))}
          </ol>
        </nav>
      ) : null}
      {version.clauses.map((c) => {
        const Tag = `h${Math.min(c.level + 1, 4)}` as "h2" | "h3" | "h4";
        const body = c.bodies[shown];
        return (
          <section key={c.key} id={c.anchor} aria-labelledby={`${c.anchor}-title`} className="policies-clause" data-level={c.level}>
            <Tag id={`${c.anchor}-title`}>
              <span className="policies-number" dir="ltr">
                {c.number}
              </span>
              <bdi>{c.titles[shown] ?? ""}</bdi>
            </Tag>
            {body ? <BodyView body={body} embedTitle={c.titles[shown] ?? ""} /> : null}
          </section>
        );
      })}
    </article>
  );
}
