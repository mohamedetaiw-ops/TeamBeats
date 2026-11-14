"use client";

import Link from "next/link";
import { useState } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { StateView } from "@/components/ui/StateView";
import { problemMessages } from "@/lib/notifications/types";
import { unitName, type OrgUnit } from "@/lib/experience/builder-types";
import { pick, type Category, type ServiceAdminView } from "@/lib/services/types";
import "./services.css";

/**
 * The services an owner manages (spec 016 US7): create a form service or a linked app, open one to build it, and keep
 * the catalogue categories. The API lists only services the owner may manage and checks every change again.
 */
export function ServiceAdminList({ initial, categories: initialCategories, units }: { initial: ServiceAdminView[]; categories: Category[]; units: OrgUnit[] }) {
  const t = useTranslations("services.admin");
  const ts = useTranslations("services");
  const locale = useLocale();
  const router = useRouter();
  const [kind, setKind] = useState<"form" | "linked">("form");
  const [nameEn, setNameEn] = useState("");
  const [nameAr, setNameAr] = useState("");
  const [owner, setOwner] = useState("");
  const [url, setUrl] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);
  const [categories, setCategories] = useState(initialCategories);
  const [categoryEn, setCategoryEn] = useState("");
  const [categoryAr, setCategoryAr] = useState("");
  const [categoryMessages, setCategoryMessages] = useState<string[]>([]);

  async function create() {
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch("/api/v1/services/admin", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ kind, names: { en: nameEn, ar: nameAr }, ownerOrgUnitId: owner || null, url: kind === "linked" ? url : null }),
      });
      if (response.ok) {
        const view = (await response.json()) as ServiceAdminView;
        router.push(`/admin/services/${view.service.id}`);
        return;
      }
      setMessages(await problemMessages(response));
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  async function addCategory() {
    setCategoryMessages([]);
    const response = await fetch("/api/v1/services/admin/categories", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ names: { en: categoryEn, ar: categoryAr }, position: categories.length }),
    });
    if (response.ok) {
      setCategories([...categories, (await response.json()) as Category]);
      setCategoryEn("");
      setCategoryAr("");
    } else {
      setCategoryMessages(await problemMessages(response));
    }
  }

  async function removeCategory(id: string) {
    const response = await fetch(`/api/v1/services/admin/categories/${id}`, { method: "DELETE" });
    if (response.ok) setCategories(categories.filter((c) => c.id !== id));
    else setCategoryMessages(await problemMessages(response));
  }

  return (
    <div className="services">
      <section className="services-section" aria-labelledby="services-admin-list">
        <h2 id="services-admin-list">{t("listTitle")}</h2>
        {initial.length === 0 ? (
          <StateView kind="empty" title={t("none")} message="" />
        ) : (
          <ul className="services-list" data-testid="services-admin-list">
            {initial.map(({ service, latest }) => (
              <li key={service.id} className="services-item">
                <Link href={`/admin/services/${service.id}`}>
                  <bdi>{pick(service.names, locale)}</bdi>
                </Link>
                <span className="services-row">
                  <span className="ui-badge">{t(`kind.${service.kind}`)}</span>
                  <span className="ui-badge">{t(`state.${service.status}`)}</span>
                  {latest?.state === "draft" && service.status === "published" ? <span className="services-meta">{t("draftPending")}</span> : null}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <form
        className="services-section services-form"
        aria-labelledby="services-admin-create"
        onSubmit={(e) => {
          e.preventDefault();
          void create();
        }}
      >
        <h2 id="services-admin-create">{t("createTitle")}</h2>
        <label className="services-select">
          {t("kindLabel")}
          <select value={kind} onChange={(e) => setKind(e.target.value as "form" | "linked")}>
            <option value="form">{t("kind.form")}</option>
            <option value="linked">{t("kind.linked")}</option>
          </select>
        </label>
        <FormField label={t("nameEn")} name="nameEn" dir="ltr" lang="en" value={nameEn} onChange={(e) => setNameEn(e.target.value)} />
        <FormField label={t("nameAr")} name="nameAr" dir="rtl" lang="ar" value={nameAr} onChange={(e) => setNameAr(e.target.value)} />
        {kind === "linked" ? <FormField label={t("url")} name="url" type="url" dir="ltr" value={url} onChange={(e) => setUrl(e.target.value)} /> : null}
        <label className="services-select">
          {t("owner")}
          <select value={owner} onChange={(e) => setOwner(e.target.value)}>
            <option value="">{t("ownerTenant")}</option>
            {units.map((u) => (
              <option key={u.id} value={u.id}>
                {unitName(u, locale)}
              </option>
            ))}
          </select>
        </label>
        {messages.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {messages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
        <div className="services-actions">
          <Button type="submit" variant="primary" busy={busy} busyLabel={t("creating")}>
            {t("create")}
          </Button>
        </div>
      </form>

      <section className="services-section" aria-labelledby="services-admin-categories">
        <h2 id="services-admin-categories">{t("categoriesTitle")}</h2>
        <ul className="services-list">
          {categories.map((c) => (
            <li key={c.id} className="services-row">
              <bdi>{pick(c.names, locale)}</bdi>
              <Button variant="quiet" onClick={() => void removeCategory(c.id)} aria-label={t("removeCategory", { name: pick(c.names, locale) })}>
                {ts("form.remove")}
              </Button>
            </li>
          ))}
        </ul>
        <div className="services-row">
          <FormField label={t("categoryEn")} name="categoryEn" dir="ltr" lang="en" value={categoryEn} onChange={(e) => setCategoryEn(e.target.value)} />
          <FormField label={t("categoryAr")} name="categoryAr" dir="rtl" lang="ar" value={categoryAr} onChange={(e) => setCategoryAr(e.target.value)} />
        </div>
        {categoryMessages.length > 0 ? (
          <div className="notice notice-error" role="alert">
            {categoryMessages.map((m) => (
              <p key={m}>{m}</p>
            ))}
          </div>
        ) : null}
        <div className="services-actions">
          <Button onClick={() => void addCategory()}>{t("addCategory")}</Button>
        </div>
      </section>
    </div>
  );
}
