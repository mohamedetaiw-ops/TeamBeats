import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { initials, isPhoneField, localName, locationName, personName, type OrgChart, type PersonView } from "@/lib/people/types";
import { Header } from "@/components/shell/Header";
import { OrgChartView } from "@/features/people/OrgChartView";
import { PeopleNav } from "@/features/people/PeopleNav";
import { PersonItem } from "@/features/people/PersonItem";
import "@/features/people/people.css";

/**
 * A person's profile (spec 010 US1 and US3): the fields the viewer may see, contact actions built only from those
 * fields (FR-015), the manager, and the org chart. A person who left shows as no longer available (410).
 */
export default async function PersonPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("people");
  const locale = await getLocale();
  const person = await apiGet<PersonView>(`/api/v1/people/${encodeURIComponent(id)}`);
  if (!person.ok && person.status !== 410) {
    notFound();
  }
  if (!person.ok) {
    return (
      <>
        <Header me={me.data} breadcrumbs={[{ label: t("title"), href: "/people" }]} />
        <main id="main" className="page">
          <h1>{t("title")}</h1>
          <p className="notice" data-testid="person-gone">
            {t("profile.gone")}
          </p>
          <PeopleNav current={null} />
        </main>
      </>
    );
  }
  const p = person.data;
  const chart = await apiGet<OrgChart>(`/api/v1/people/${encodeURIComponent(id)}/org-chart`);
  const name = personName(p, locale);
  const email = p.fields.find((f) => f.key === "email")?.value;
  const phones = p.fields.filter((f) => isPhoneField(f.key));
  const label = (key: string, names: Record<string, string> | null) => (names ? localName(names, locale, key) : t(`field.${key}` as "field.email"));
  const show = (key: string, value: string) =>
    key === "hireDate" ? new Intl.DateTimeFormat(locale, { dateStyle: "long", timeZone: "UTC" }).format(new Date(`${value}T00:00:00Z`)) : value;

  return (
    <>
      <Header
        me={me.data}
        breadcrumbs={[
          { label: t("title"), href: "/people" },
          { label: name, href: `/people/${p.id}` },
        ]}
      />
      <main id="main" className="page people">
        <div className="people-head">
          <span className="people-avatar people-avatar-large" aria-hidden="true">
            {initials(name)}
          </span>
          <div className="people-item-text">
            <h1>
              <bdi>{name}</bdi>
            </h1>
            <span className="people-meta">
              {p.unit ? <bdi>{localName(p.unit.names, locale)}</bdi> : null}
              {p.location ? <bdi>{locationName(p.location, locale)}</bdi> : null}
            </span>
          </div>
          {p.isSelf ? <Link href="/people/me">{t("profile.editOwn")}</Link> : null}
        </div>
        <PeopleNav current={null} />

        {email || phones.length > 0 ? (
          <section aria-labelledby="person-contact" className="people-section">
            <h2 id="person-contact">{t("profile.contact")}</h2>
            <div className="people-actions">
              {email ? (
                <a className="button button-secondary" href={`mailto:${email}`} aria-label={t("profile.email", { name })}>
                  <span dir="ltr">{email}</span>
                </a>
              ) : null}
              {phones.map((f) => (
                <a key={f.key} className="button button-secondary" href={`tel:${f.value.replace(/[^\d+]/gu, "")}`} aria-label={t("profile.call", { number: f.value })}>
                  {label(f.key, f.names)}: <span dir="ltr">{f.value}</span>
                </a>
              ))}
            </div>
          </section>
        ) : null}

        <section aria-labelledby="person-details" className="people-section">
          <h2 id="person-details">{t("profile.details")}</h2>
          <dl className="people-details" data-testid="person-fields">
            {p.fields
              .filter((f) => f.key !== "email")
              .map((f) => (
                <div key={f.key} className="people-pair">
                  <dt>{label(f.key, f.names)}</dt>
                  <dd>{isPhoneField(f.key) || f.key === "employeeNumber" ? <span dir="ltr">{f.value}</span> : <bdi>{show(f.key, f.value)}</bdi>}</dd>
                </div>
              ))}
            {p.units.length > 1 ? (
              <>
                <dt>{t("profile.units")}</dt>
                <dd>{p.units.map((u) => localName(u.names, locale)).join(t("separator"))}</dd>
              </>
            ) : null}
            <dt>{t("profile.manager")}</dt>
            <dd>{p.manager ? <PersonItem person={p.manager} locale={locale} /> : t("profile.noManager")}</dd>
            <dt>{t("org.reports")}</dt>
            <dd>{t("profile.reports", { count: p.directReports })}</dd>
          </dl>
        </section>

        {chart.ok ? <OrgChartView chart={chart.data} reportsHref={p.isSelf ? "/people/team" : null} /> : null}
      </main>
    </>
  );
}
