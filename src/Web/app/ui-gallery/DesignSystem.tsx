"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Avatar } from "@/components/ui/Avatar";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { Icon, iconNames } from "@/components/ui/Icon";
import { Media } from "@/components/ui/Media";
import { Tabs } from "@/components/ui/Tabs";

/** Token names shown as swatches; they are identifiers, not interface text, so they appear as code. */
const swatches = [
  "--tb-color-accent-base",
  "--tb-color-accent-subtle",
  "--tb-color-highlight-base",
  "--tb-color-surface-base",
  "--tb-color-surface-raised",
  "--tb-color-surface-sunken",
  "--tb-color-text-primary",
  "--tb-color-text-secondary",
  "--tb-color-shell-background",
  "--tb-color-nav-background",
  "--tb-color-state-success",
  "--tb-color-state-warning",
  "--tb-color-state-danger",
  "--tb-color-state-info",
] as const;

const people = ["a", "b", "c", "d"] as const;

/**
 * The living reference of the TeamBeats design system (docs/design-system): foundations, components and template
 * patterns rendered with the tenant's effective design, so the same page shows Beats, Atlas or Pulse. The Playwright
 * design suite runs accessibility, responsive and visual checks against it in every design and both directions.
 */
export function DesignSystem() {
  const t = useTranslations("design_system");
  const [bio, setBio] = useState("");
  return (
    <div className="gallery" data-testid="design-system">
      <section aria-labelledby="ds-colour">
        <h2 id="ds-colour">{t("colour.title")}</h2>
        <p className="notice">{t("colour.intro")}</p>
        <ul className="gallery-swatches">
          {swatches.map((s) => (
            <li key={s}>
              <span className="gallery-swatch" style={{ background: `var(${s})` }} />
              <code dir="ltr">{s.replace("--tb-color-", "")}</code>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="ds-type" className="gallery-type">
        <h2 id="ds-type">{t("type.title")}</h2>
        <p className="notice">{t("type.intro")}</p>
        <p style={{ fontFamily: "var(--tb-font-family-heading)", fontSize: "var(--tb-font-size-xxl)", fontWeight: "var(--tb-font-weight-bold)" }}>
          {t("type.display")}
        </p>
        <p style={{ fontFamily: "var(--tb-font-family-heading)", fontSize: "var(--tb-font-size-xl)", fontWeight: "var(--tb-font-weight-bold)" }}>
          {t("type.heading")}
        </p>
        <p style={{ fontSize: "var(--tb-font-size-lg)" }}>{t("type.lead")}</p>
        <p>{t("type.body")}</p>
        <p className="notice" style={{ fontSize: "var(--tb-font-size-sm)" }}>
          {t("type.caption")}
        </p>
      </section>

      <section aria-labelledby="ds-icons">
        <h2 id="ds-icons">{t("icons.title")}</h2>
        <p className="notice">{t("icons.intro")}</p>
        <ul className="gallery-icons">
          {iconNames.map((name) => (
            <li key={name}>
              <Icon name={name} size={24} />
              <code dir="ltr">{name}</code>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="ds-actions" data-context="actions">
        <h2 id="ds-actions">{t("actions.title")}</h2>
        <p className="notice">{t("actions.intro")}</p>
        <div className="gallery-row">
          <Button variant="primary">{t("actions.primary")}</Button>
          <Button variant="secondary">{t("actions.secondary")}</Button>
          <Button variant="quiet">{t("actions.quiet")}</Button>
          <Button variant="destructive">{t("actions.destructive")}</Button>
          <Button variant="primary" busy busyLabel={t("actions.busy")}>
            {t("actions.primary")}
          </Button>
          <a href="#ds-actions" className="button button-secondary">
            <Icon name="download" />
            <span>{t("actions.link")}</span>
          </a>
        </div>
      </section>

      <section aria-labelledby="ds-fields" data-context="fields">
        <h2 id="ds-fields">{t("fields.title")}</h2>
        <form className="gallery-cards" onSubmit={(e) => e.preventDefault()} noValidate>
          <div>
            <FormField label={t("fields.name")} name="ds-name" hint={t("fields.nameHint")} autoComplete="name" />
            <FormField label={t("fields.email")} name="ds-email" type="email" dir="ltr" error={t("fields.emailError")} defaultValue="layla@" />
          </div>
          <div>
            <SelectField
              label={t("fields.department")}
              name="ds-department"
              options={[
                { value: "hr", label: t("fields.departments.hr") },
                { value: "finance", label: t("fields.departments.finance") },
                { value: "it", label: t("fields.departments.it") },
              ]}
            />
            <TextAreaField
              label={t("fields.bio")}
              name="ds-bio"
              maxLength={280}
              value={bio}
              onChange={(e) => setBio(e.target.value)}
              counter={t("fields.counter", { count: 280 - bio.length })}
            />
          </div>
        </form>
      </section>

      <section aria-labelledby="ds-feedback">
        <h2 id="ds-feedback">{t("feedback.title")}</h2>
        <div className="gallery-row">
          <Badge>{t("feedback.badges.draft")}</Badge>
          <Badge tone="accent">{t("feedback.badges.new")}</Badge>
          <Badge tone="info" icon="info">
            {t("feedback.badges.review")}
          </Badge>
          <Badge tone="success" icon="check">
            {t("feedback.badges.published")}
          </Badge>
          <Badge tone="warning" icon="clock">
            {t("feedback.badges.due")}
          </Badge>
          <Badge tone="danger" icon="alert">
            {t("feedback.badges.overdue")}
          </Badge>
        </div>
        <div className="gallery-cards">
          <Alert tone="info" title={t("feedback.alerts.infoTitle")}>
            <p>{t("feedback.alerts.infoText")}</p>
          </Alert>
          <Alert tone="success" title={t("feedback.alerts.successTitle")}>
            <p>{t("feedback.alerts.successText")}</p>
          </Alert>
          <Alert tone="warning" title={t("feedback.alerts.warningTitle")}>
            <p>{t("feedback.alerts.warningText")}</p>
          </Alert>
          <Alert tone="danger" title={t("feedback.alerts.dangerTitle")} action={<Button variant="secondary">{t("feedback.alerts.fix")}</Button>}>
            <p>{t("feedback.alerts.dangerText")}</p>
          </Alert>
        </div>
        <div className="gallery-row">
          {people.map((p) => (
            <Avatar key={p} name={t(`people.${p}`)} />
          ))}
          <Avatar name={t("people.a")} size="lg" />
          <Avatar name={t("people.b")} size="sm" />
        </div>
      </section>

      <section aria-labelledby="ds-cards">
        <h2 id="ds-cards">{t("cards.title")}</h2>
        <div className="gallery-cards">
          <Card
            title={t("cards.news.title")}
            href="#ds-cards"
            eyebrow={<Badge tone="accent">{t("cards.news.tag")}</Badge>}
            media={<Media src="/design-system/team.svg" alt={t("cards.news.alt")} />}
          >
            <p>{t("cards.news.text")}</p>
          </Card>
          <Card
            title={t("cards.event.title")}
            href="#ds-cards"
            eyebrow={t("cards.event.when")}
            media={<Media src="/design-system/event.svg" alt={t("cards.event.alt")} />}
          >
            <p>{t("cards.event.text")}</p>
          </Card>
          <Card
            title={t("cards.service.title")}
            tone="accent"
            footer={
              <Button variant="primary">
                <span>{t("cards.service.action")}</span>
              </Button>
            }
          >
            <p>{t("cards.service.text")}</p>
          </Card>
        </div>
      </section>

      <section aria-labelledby="ds-imagery">
        <h2 id="ds-imagery">{t("imagery.title")}</h2>
        <div className="gallery-cards">
          <Media src="/design-system/office.svg" ratio="4:3" alt={t("imagery.office")} caption={t("imagery.caption")} />
          <Media src="/design-system/team.svg" ratio="1:1" decorative />
        </div>
      </section>

      <section aria-labelledby="ds-tabs">
        <h2 id="ds-tabs">{t("tabs.title")}</h2>
        <Tabs
          label={t("tabs.label")}
          tabs={[
            { key: "overview", label: t("tabs.overview"), content: <p>{t("tabs.overviewText")}</p> },
            { key: "documents", label: t("tabs.documents"), content: <p>{t("tabs.documentsText")}</p> },
            { key: "people", label: t("tabs.people"), content: <p>{t("tabs.peopleText")}</p> },
          ]}
        />
      </section>

      <section aria-labelledby="ds-templates">
        <h2 id="ds-templates">{t("templates.title")}</h2>
        <p className="notice">{t("templates.intro")}</p>
        <div className="gallery-cards">
          {(["home", "article", "listing", "form", "dashboard", "profile"] as const).map((k) => (
            <Card key={k} title={t(`templates.items.${k}.title`)} tone="sunken" eyebrow={<Icon name="services" size={20} />}>
              <p>{t(`templates.items.${k}.text`)}</p>
            </Card>
          ))}
        </div>
      </section>
    </div>
  );
}
