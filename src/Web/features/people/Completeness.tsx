"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Widget } from "@/components/ui/Widget";
import type { Completeness, OwnProfile } from "@/lib/people/types";
import "./people.css";

/** Where each missing item is filled on the own profile page. */
const anchors: Record<string, string> = { photo: "#own-photo", skills: "#own-skills" };

/**
 * How complete the own profile is (spec 010 R2 US8 scenario 1, FR-028): the share filled and a link to each missing
 * item, until it is complete or dismissed for 90 days. The API works out what counts.
 */
export function CompletenessPrompt({ initial, fieldLabel }: { initial: Completeness; fieldLabel: Record<string, string> }) {
  const t = useTranslations("people.completeness");
  const [view, setView] = useState(initial);
  const [busy, setBusy] = useState(false);
  if (!view.show) return null;

  async function dismiss() {
    setBusy(true);
    try {
      const response = await fetch("/api/v1/me/profile/completeness:dismiss", { method: "POST" });
      if (response.ok) {
        const own = (await response.json()) as OwnProfile;
        if (own.completeness) setView(own.completeness);
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <Widget title={t("title", { percent: String(view.percent) })}>
      <div className="people-completeness" data-testid="profile-completeness">
        <progress max={100} value={view.percent} aria-label={t("progress", { percent: String(view.percent) })} />
        <p className="people-meta">{t("intro")}</p>
        <ul className="people-completeness-list">
          {view.missing.map((key) => (
            <li key={key}>
              <a href={anchors[key] ?? "#own-profile"}>{key === "photo" ? t("photo") : key === "skills" ? t("skills") : (fieldLabel[key] ?? key)}</a>
            </li>
          ))}
        </ul>
        <div className="people-actions">
          <Button size="sm" variant="quiet" busy={busy} busyLabel={t("working")} onClick={() => void dismiss()}>
            {t("dismiss")}
          </Button>
        </div>
      </div>
    </Widget>
  );
}
