"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/Button";
import { Checkbox } from "@/components/ui/Checkbox";
import type { MembershipPolicy } from "@/lib/admin/types";
import { ResultAlert, useAdminAction, useKeyLabel } from "./shared";

const RETURN = "/admin/tenant";

/**
 * Default access by membership type (spec 004 section 6): every member gets the Employee role across the workspace
 * unless the tenant removes it for a type, such as contractors. Guests never get it, so their row is fixed. The API
 * decides who may change this; the panel is shown only when it returned the policies.
 */
export function MembershipPoliciesAdmin({ initial }: { initial: MembershipPolicy[] }) {
  const t = useTranslations("admin.tenant.membership");
  const typeName = useKeyLabel("admin.users.membershipTypes");
  const { busy, result, run } = useAdminAction(RETURN);
  const [saved, setSaved] = useState(initial);
  const [values, setValues] = useState<Record<string, boolean>>(Object.fromEntries(initial.map((p) => [p.membershipType, p.defaultEmployeeRole])));
  const changed = saved.filter((p) => !p.fixed && values[p.membershipType] !== p.defaultEmployeeRole);

  async function save() {
    for (const p of changed) {
      const outcome = await run("save", "PUT", `/api/v1/tenant/membership-policies/${p.membershipType}`, { defaultEmployeeRole: values[p.membershipType] }, t("saved"));
      if (!outcome.ok) return;
    }
    setSaved(saved.map((p) => ({ ...p, defaultEmployeeRole: values[p.membershipType] ?? p.defaultEmployeeRole })));
  }

  return (
    <section className="admin-panel" aria-labelledby="tenant-membership-title">
      <h2 id="tenant-membership-title">{t("title")}</h2>
      <p className="admin-meta">{t("intro")}</p>
      <ResultAlert result={result} />
      <fieldset className="admin-fieldset">
        <legend>{t("legend")}</legend>
        <div className="admin-choices">
          {saved.map((p) => (
            <Checkbox
              key={p.membershipType}
              name={`membership-${p.membershipType}`}
              label={typeName(p.membershipType)}
              hint={p.fixed ? t("fixed") : values[p.membershipType] ? t("on") : t("off")}
              checked={values[p.membershipType] ?? false}
              disabled={p.fixed}
              onChange={(e) => setValues({ ...values, [p.membershipType]: e.target.checked })}
            />
          ))}
        </div>
      </fieldset>
      <div className="admin-row">
        <Button variant="primary" disabled={changed.length === 0} busy={busy === "save"} busyLabel={t("saving")} onClick={() => void save()}>
          {t("save")}
        </Button>
      </div>
    </section>
  );
}
