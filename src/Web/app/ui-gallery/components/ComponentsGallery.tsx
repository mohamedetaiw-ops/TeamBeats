"use client";

import { useState, useSyncExternalStore } from "react";
import { useTranslations } from "next-intl";
import { Accordion } from "@/components/ui/Accordion";
import { Announcement } from "@/components/ui/Announcement";
import { AvatarGroup } from "@/components/ui/Avatar";
import { Button, ButtonLink } from "@/components/ui/Button";
import { ButtonGroup } from "@/components/ui/ButtonGroup";
import { Checkbox } from "@/components/ui/Checkbox";
import { Chip, TagGroup } from "@/components/ui/Chip";
import { IconButton } from "@/components/ui/IconButton";
import { Pagination } from "@/components/ui/Pagination";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import { Slider } from "@/components/ui/Slider";
import { Divider, Kbd, Meter, Progress, Spinner, StatusDot } from "@/components/ui/Status";
import { Switch } from "@/components/ui/Switch";
import { AssistantGallery } from "./AssistantGallery";
import { ChartsGallery } from "./ChartsGallery";
import { DataGallery } from "./DataGallery";
import { InputsGallery } from "./InputsGallery";
import { OverlaysGallery } from "./OverlaysGallery";

const team = ["Layla Hassan", "Omar Farouk", "Sara Nasser", "Khalid Mansour", "Huda Salem", "Yousef Adel"].map((name) => ({ name }));

/** Key names are the same in every language, so they are identifiers, not messages. */
const searchKeys = ["Ctrl", "K"] as const;

const noSubscription = () => () => {};

/** The extended catalogue, rendered with realistic text so the design suite checks it in every design and direction. */
export function ComponentsGallery() {
  const t = useTranslations("components_gallery");
  const [view, setView] = useState("week");
  const [filters, setFilters] = useState(["finance", "dubai", "thisYear"]);
  const [topics, setTopics] = useState<string[]>(["topicNews"]);
  const [documents, setDocuments] = useState([true, false, false]);
  const [dismissed, setDismissed] = useState(false);
  const chosen = documents.filter(Boolean).length;
  const hydrated = useSyncExternalStore(noSubscription, () => true, () => false);

  return (
    <div className="gallery" data-testid="components-gallery" data-hydrated={hydrated}>
      <section aria-labelledby="cg-actions">
        <h2 id="cg-actions">{t("actions.title")}</h2>
        <p className="notice">{t("actions.intro")}</p>
        <div className="gallery-stack">
          <div className="gallery-row">
            <Button variant="primary" icon="check">
              {t("actions.save")}
            </Button>
            <Button icon="plus">{t("actions.add")}</Button>
            <Button variant="quiet" iconEnd="arrow-forward">
              {t("actions.next")}
            </Button>
            <Button variant="destructive" icon="trash">
              {t("actions.delete")}
            </Button>
          </div>
          <div className="gallery-row">
            <Button variant="primary" size="sm">
              {t("actions.small")}
            </Button>
            <Button size="sm" icon="download">
              {t("actions.export")}
            </Button>
            <Button variant="primary" size="xs">
              {t("actions.compact")}
            </Button>
            <Button size="xs" icon="filter">
              {t("actions.filter")}
            </Button>
            <ButtonLink href="#cg-actions" size="sm" iconEnd="external">
              {t("actions.open")}
            </ButtonLink>
          </div>
          <div className="gallery-row">
            <IconButton icon="edit" label={t("actions.edit")} variant="secondary" />
            <IconButton icon="more" label={t("actions.more")} />
            <IconButton icon="filter" label={t("actions.filter")} size="sm" variant="secondary" />
            <IconButton icon="trash" label={t("actions.delete")} size="sm" variant="destructive" />
            <ButtonGroup label={t("actions.group")}>
              {(["day", "week", "month"] as const).map((key) => (
                <Button key={key} size="sm" aria-pressed={view === key} onClick={() => setView(key)}>
                  {t(`actions.${key}`)}
                </Button>
              ))}
            </ButtonGroup>
          </div>
        </div>
      </section>

      <section aria-labelledby="cg-selection">
        <h2 id="cg-selection">{t("selection.title")}</h2>
        <p className="notice">{t("selection.intro")}</p>
        <div className="gallery-columns">
          <div className="gallery-stack">
            <Checkbox label={t("selection.notify")} hint={t("selection.notifyHint")} name="cg-notify" defaultChecked />
            <Checkbox
              label={t("selection.all")}
              name="cg-all"
              checked={chosen === documents.length}
              indeterminate={chosen > 0 && chosen < documents.length}
              onChange={(e) => setDocuments(documents.map(() => e.currentTarget.checked))}
            />
            <Checkbox label={t("selection.terms")} name="cg-terms" error={t("selection.termsError")} />
            <Checkbox label={t("selection.disabled")} name="cg-disabled" disabled defaultChecked />
            <Switch label={t("selection.presence")} name="cg-presence" defaultChecked />
            <Switch label={t("selection.digest")} hint={t("selection.digestHint")} name="cg-digest" />
            <Switch label={t("selection.focus")} hint={t("selection.focusHint")} name="cg-focus" card defaultChecked />
          </div>
          <div className="gallery-stack">
            <RadioGroup
              legend={t("selection.plan")}
              name="cg-plan"
              cards
              defaultValue="hybrid"
              options={(["office", "hybrid", "remote"] as const).map((key) => ({
                value: key,
                label: t(`selection.${key}`),
                hint: t(`selection.${key}Hint`),
              }))}
            />
            <SegmentedControl
              label={t("selection.view")}
              name="cg-period"
              defaultValue="weekly"
              options={(["daily", "weekly", "monthly", "yearly"] as const).map((key) => ({ value: key, label: t(`selection.${key}`) }))}
            />
            <Slider
              label={t("selection.textSize")}
              hint={t("selection.textSizeHint")}
              min={100}
              max={200}
              step={25}
              defaultValue={125}
              format={(value) => t("selection.textSizeValue", { value })}
            />
          </div>
          <div className="gallery-stack">
            <TagGroup
              label={t("selection.filters")}
              empty={t("selection.noFilters")}
              items={filters.map((key) => ({ key, label: t(`selection.${key}`) }))}
              onRemove={(key) => setFilters(filters.filter((f) => f !== key))}
            />
            <div className="gallery-row" role="group" aria-label={t("selection.topics")}>
              {(["topicNews", "topicEvents", "topicPolicies", "topicPeople"] as const).map((key) => (
                <Chip
                  key={key}
                  selected={topics.includes(key)}
                  onToggle={(on) => setTopics(on ? [...topics, key] : topics.filter((k) => k !== key))}
                >
                  {t(`selection.${key}`)}
                </Chip>
              ))}
            </div>
            <div className="gallery-row" role="group" aria-label={t("selection.chipTones")}>
              <Chip>{t("selection.draft")}</Chip>
              <Chip tone="accent" icon="people">
                {t("selection.team")}
              </Chip>
              <Chip tone="success" icon="check">
                {t("selection.approved")}
              </Chip>
              <Chip tone="info" icon="clock">
                {t("selection.review")}
              </Chip>
              <Chip tone="danger" icon="alert">
                {t("selection.blocked")}
              </Chip>
            </div>
          </div>
        </div>
      </section>

      <section aria-labelledby="cg-status">
        <h2 id="cg-status">{t("status.title")}</h2>
        <p className="notice">{t("status.intro")}</p>
        <div className="gallery-columns">
          <div className="gallery-stack">
            <div className="gallery-row">
              <StatusDot tone="success" pulse>
                {t("status.online")}
              </StatusDot>
              <StatusDot tone="info">{t("status.syncing")}</StatusDot>
              <StatusDot tone="warning">{t("status.away")}</StatusDot>
              <StatusDot tone="danger">{t("status.failed")}</StatusDot>
              <StatusDot>{t("status.offline")}</StatusDot>
            </div>
            <p className="gallery-row">
              <span>{t("status.shortcut")}</span>
              {searchKeys.map((key) => (
                <Kbd key={key}>{key}</Kbd>
              ))}
            </p>
            <Divider label={t("status.or")} />
            <div className="gallery-row">
              <Spinner label={t("status.loading")} size="sm" />
              <Spinner label={t("status.loading")} />
              <Spinner label={t("status.loading")} size="lg" />
              <AvatarGroup label={t("status.team")} people={team} more={(count) => t("status.more", { count })} />
            </div>
          </div>
          <div className="gallery-stack">
            <Progress label={t("status.upload")} value={24} max={60} valueText={t("status.uploadValue", { done: 24, total: 60 })} />
            <Progress label={t("status.preparing")} />
            <Meter label={t("status.storage")} value={12} max={50} valueText={t("status.storageValue", { used: 12, total: 50 })} />
            <Meter label={t("status.quota")} value={410} max={500} tone="warning" valueText={t("status.quotaValue", { used: 410, total: 500 })} />
            <Meter label={t("status.leave")} value={22} max={22} tone="danger" valueText={t("status.leaveValue", { used: 22, total: 22 })} />
          </div>
        </div>
      </section>

      <section aria-labelledby="cg-structure">
        <h2 id="cg-structure">{t("structure.title")}</h2>
        <p className="notice">{t("structure.intro")}</p>
        <div className="gallery-columns">
          <Accordion
            exclusive="cg-faq"
            items={[
              { key: "q1", title: t("structure.q1"), content: <p>{t("structure.a1")}</p>, open: true },
              { key: "q2", title: t("structure.q2"), content: <p>{t("structure.a2")}</p> },
              { key: "q3", title: t("structure.q3"), content: <p>{t("structure.a3")}</p> },
            ]}
          />
          <div className="gallery-stack">
            {dismissed ? (
              <p className="notice" role="status">
                {t("structure.dismissed")}
              </p>
            ) : (
              <Announcement
                title={t("structure.announceTitle")}
                action={
                  <ButtonLink href="#cg-structure" size="sm">
                    {t("structure.announceAction")}
                  </ButtonLink>
                }
                onDismiss={() => setDismissed(true)}
              >
                {t("structure.announceText")}
              </Announcement>
            )}
            <Pagination label={t("structure.pages")} page={6} pageCount={20} hrefFor={(page) => `#page-${page}`} />
          </div>
        </div>
      </section>
      <OverlaysGallery />
      <InputsGallery />
      <DataGallery />
      <AssistantGallery />
      <ChartsGallery />
    </div>
  );
}
