"use client";

import { useId, useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { problemMessages } from "@/lib/notifications/types";
import { exemptionsHref, type ExemptionView } from "@/lib/policies/types";
import { Button } from "@/components/ui/Button";
import { FormField, SelectField, TextAreaField } from "@/components/ui/FormField";
import { RadioGroup } from "@/components/ui/RadioGroup";
import { PersonPicker } from "@/features/services/PersonPicker";
import "./policies.css";

type Scope = "me" | "person" | "unit";

/**
 * Asks for an exemption to a policy (spec 025 US7 scenario 1): a reason and an end date, for oneself or, when the API
 * says the person may, for someone else or a unit. The request goes to the policy owner and then to compliance; the
 * API checks every value.
 */
export function ExemptionRequestForm({ policyId, requestFor, units }: { policyId: string; requestFor: boolean; units: { value: string; label: string }[] }) {
  const t = useTranslations("policies.exemptions");
  const ids = useId();
  const router = useRouter();
  const [scope, setScope] = useState<Scope>("me");
  const [person, setPerson] = useState<string | null>(null);
  const [unit, setUnit] = useState(units[0]?.value ?? "");
  const [reason, setReason] = useState("");
  const [endsOn, setEndsOn] = useState("");
  const [busy, setBusy] = useState(false);
  const [messages, setMessages] = useState<string[]>([]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (scope === "person" && !person) {
      setMessages([t("choosePerson")]);
      return;
    }
    setBusy(true);
    setMessages([]);
    try {
      const response = await fetch(`/api/v1/policies/${encodeURIComponent(policyId)}/exemptions`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          scope: scope === "unit" ? "unit" : "person",
          userId: scope === "person" ? person : null,
          unitId: scope === "unit" ? unit || null : null,
          reason: reason.trim(),
          endsOn: endsOn || null,
        }),
      });
      if (!response.ok) {
        setMessages(await problemMessages(response));
        return;
      }
      const created = (await response.json()) as ExemptionView;
      setReason("");
      setEndsOn("");
      router.replace(`${exemptionsHref(policyId)}?asked=${encodeURIComponent(created.id)}`);
      router.refresh();
    } catch {
      setMessages([t("failed")]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="policies-card" aria-labelledby={`${ids}-title`} onSubmit={(e) => void submit(e)} noValidate data-testid="exemption-form">
      <h2 id={`${ids}-title`}>{t("requestTitle")}</h2>
      <p className="policies-meta">{t("requestIntro")}</p>
      {requestFor ? (
        <RadioGroup
          legend={t("for")}
          name="exemptionScope"
          value={scope}
          onChange={(e) => setScope(e.target.value as Scope)}
          options={[
            { value: "me", label: t("forMe") },
            { value: "person", label: t("forPerson") },
            ...(units.length > 0 ? [{ value: "unit", label: t("forUnit"), hint: t("forUnitHint") }] : []),
          ]}
        />
      ) : null}
      {scope === "person" ? <PersonPicker label={t("person")} value={person} onChange={setPerson} /> : null}
      {scope === "unit" ? <SelectField label={t("unit")} name="exemptionUnit" value={unit} onChange={(e) => setUnit(e.target.value)} options={units} /> : null}
      <TextAreaField label={t("reason")} name="exemptionReason" value={reason} maxLength={2000} required onChange={(e) => setReason(e.target.value)} hint={t("reasonHint")} />
      <FormField label={t("endsOn")} name="exemptionEndsOn" type="date" value={endsOn} required onChange={(e) => setEndsOn(e.target.value)} hint={t("endsOnHint")} />
      {messages.length > 0 ? (
        <div className="notice notice-error" role="alert">
          {messages.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      ) : null}
      <div className="button-row">
        <Button type="submit" variant="primary" busy={busy} busyLabel={t("busy")}>
          {t("submit")}
        </Button>
      </div>
    </form>
  );
}
