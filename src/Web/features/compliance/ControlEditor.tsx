"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { ResultAlert, useAdminAction } from "@/features/admin/shared";
import { PersonPicker } from "@/features/services/PersonPicker";
import { complianceApi, controlHref, controlStatuses, pick, type ControlDetail } from "@/lib/compliance";

type PolicyItem = { id: string; code: string; names: Record<string, string> };

const guid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

/**
 * Changes to a control (FR-022). Its owner moves the status up to "evidence ready" and writes a note; compliance
 * officers also set the owner, the due date, the linked policies and documents, and mark it reviewed. The API decides
 * which of these the person may change, and the form offers only those.
 */
export function ControlEditor({ detail }: { detail: ControlDetail }) {
  const t = useTranslations("compliance.control");
  const locale = useLocale();
  const router = useRouter();
  const action = useAdminAction(controlHref(detail.control.id));
  const c = detail.control;
  const [status, setStatus] = useState<string>(c.status);
  const [note, setNote] = useState(detail.note ?? "");
  const [owner, setOwner] = useState<string | null>(c.owner?.userId ?? null);
  const [due, setDue] = useState(c.dueOn ?? "");
  const [policyIds, setPolicyIds] = useState(detail.policies.map((p) => p.id));
  const [documentIds, setDocumentIds] = useState(detail.documents.map((d) => d.id));
  const [policyQuery, setPolicyQuery] = useState("");
  const [policyResults, setPolicyResults] = useState<PolicyItem[] | null>(null);
  const [documentLink, setDocumentLink] = useState("");
  const [linkError, setLinkError] = useState<string | null>(null);
  const rights = detail.rights;

  const statuses = controlStatuses.filter((s) => rights.canReview || (s !== "reviewed" && c.status !== "reviewed") || s === c.status);

  async function save(e: FormEvent) {
    e.preventDefault();
    const body: Record<string, unknown> = { status, note, version: c.version };
    if (rights.canEdit) {
      Object.assign(body, {
        ownerId: owner,
        clearOwner: owner === null,
        dueOn: due || null,
        clearDue: !due,
        policyIds,
        documentIds,
      });
    }
    const outcome = await action.run("save", "PUT", `${complianceApi}/controls/${c.id}`, body, t("saved"));
    if (outcome.ok) router.refresh();
  }

  async function searchPolicies() {
    try {
      const response = await fetch(`/api/v1/policies?q=${encodeURIComponent(policyQuery.trim())}`, { cache: "no-store" });
      setPolicyResults(response.ok ? ((await response.json()) as PolicyItem[]).slice(0, 8) : []);
    } catch {
      setPolicyResults([]);
    }
  }

  function addDocument() {
    const id = documentLink.match(guid)?.[0]?.toLowerCase();
    if (!id) {
      setLinkError(t("documentLinkInvalid"));
      return;
    }
    setLinkError(null);
    setDocumentIds((list) => (list.includes(id) ? list : [...list, id]));
    setDocumentLink("");
  }

  if (!rights.canChangeStatus) return null;

  const policyName = (id: string) => {
    const known = detail.policies.find((p) => p.id === id) ?? policyResults?.map((p) => ({ id: p.id, code: p.code, names: p.names, available: true })).find((p) => p.id === id);
    return known?.available ? `${known.code ?? ""} ${pick(known.names, locale)}`.trim() : t("unavailable");
  };
  const documentName = (id: string) => {
    const known = detail.documents.find((d) => d.id === id);
    return known ? (known.available ? pick(known.names, locale) : t("unavailable")) : t("newDocument");
  };

  return (
    <form className="compliance-section" onSubmit={(e) => void save(e)} aria-labelledby="control-edit">
      <h2 id="control-edit">{t("editTitle")}</h2>
      <ResultAlert result={action.result} />
      <div className="admin-grid">
        <SelectField label={t("status")} name="status" value={status} onChange={(e) => setStatus(e.target.value)} options={statuses.map((s) => ({ value: s, label: t(`statuses.${s}`) }))} />
        {rights.canEdit ? <FormField label={t("due")} name="due" type="date" value={due} onChange={(e) => setDue(e.target.value)} /> : null}
      </div>
      {!rights.canReview ? <p className="compliance-meta">{t("reviewHint")}</p> : null}
      <TextAreaField label={t("note")} name="note" maxLength={2000} value={note} onChange={(e) => setNote(e.target.value)} />
      {rights.canEdit ? (
        <>
          <PersonPicker label={t("owner")} value={owner} onChange={setOwner} />
          <fieldset>
            <legend>{t("policies")}</legend>
            <ul className="compliance-list">
              {policyIds.map((id) => (
                <li key={id} className="compliance-row">
                  <bdi>{policyName(id)}</bdi>
                  <Button size="sm" icon="close" onClick={() => setPolicyIds((list) => list.filter((p) => p !== id))}>
                    {t("remove")}
                  </Button>
                </li>
              ))}
            </ul>
            <div className="ui-control-row">
              <FormField label={t("findPolicy")} name="policyQuery" value={policyQuery} onChange={(e) => setPolicyQuery(e.target.value)} />
              <Button icon="search" onClick={() => void searchPolicies()}>
                {t("search")}
              </Button>
            </div>
            {policyResults ? (
              policyResults.length === 0 ? (
                <p className="compliance-meta">{t("noPolicies")}</p>
              ) : (
                <ul className="compliance-list" aria-label={t("policyResults")}>
                  {policyResults.map((p) => (
                    <li key={p.id} className="compliance-row">
                      <bdi>
                        {p.code} {pick(p.names, locale)}
                      </bdi>
                      <Button size="sm" icon="plus" disabled={policyIds.includes(p.id)} onClick={() => setPolicyIds((list) => [...list, p.id])}>
                        {t("link")}
                      </Button>
                    </li>
                  ))}
                </ul>
              )
            ) : null}
          </fieldset>
          <fieldset>
            <legend>{t("documents")}</legend>
            <ul className="compliance-list">
              {documentIds.map((id) => (
                <li key={id} className="compliance-row">
                  <bdi>{documentName(id)}</bdi>
                  <Button size="sm" icon="close" onClick={() => setDocumentIds((list) => list.filter((d) => d !== id))}>
                    {t("remove")}
                  </Button>
                </li>
              ))}
            </ul>
            <div className="ui-control-row">
              <FormField
                label={t("documentLink")}
                hint={t("documentLinkHint")}
                name="documentLink"
                dir="ltr"
                value={documentLink}
                error={linkError}
                onChange={(e) => setDocumentLink(e.target.value)}
              />
              <Button icon="link" onClick={addDocument}>
                {t("link")}
              </Button>
            </div>
          </fieldset>
        </>
      ) : (
        <p className="compliance-meta">
          <Badge tone="info">{t("ownerBadge")}</Badge> {t("ownerHint")}
        </p>
      )}
      <div className="ui-control-row">
        <Button type="submit" variant="primary" busy={action.busy === "save"} busyLabel={t("working")}>
          {t("save")}
        </Button>
      </div>
    </form>
  );
}
