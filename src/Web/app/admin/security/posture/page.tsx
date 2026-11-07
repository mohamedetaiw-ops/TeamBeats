import Link from "next/link";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { Posture } from "@/lib/security/types";
import { SecurityTabs } from "@/features/security/SecurityTabs";
import { securitySection } from "@/features/security/guard";
import "@/features/security/security.css";

const CHECKS = ["sso_configured", "mfa_required", "ip_rules", "admin_area_rule", "step_up_window", "idle_timeout", "absolute_timeout", "dev_sign_in_absent"];

/** Security posture (spec 026 FR-017, T016): settings weaker than the recommended baseline, each with a way to fix it. */
export default async function SecurityPosturePage() {
  const section = await securitySection();
  const posture = await apiGet<Posture>("/api/v1/security/posture");
  if (!posture.ok) notFound();
  const t = await getTranslations("security.posture");
  const checks = posture.data.checks.filter((c) => CHECKS.includes(c.key));
  return (
    <>
      <h1>{t("title")}</h1>
      <SecurityTabs current="posture" openAlerts={section.badge} />
      <p className="notice">{t("summary", { passed: posture.data.passed, total: posture.data.total })}</p>
      <ul className="security-list">
        {checks.map((check) => (
          <li key={check.key} className="security-item">
            <div className="security-row">
              <h2>{t(`checks.${check.key}.title`)}</h2>
              <span className="security-state" data-state={check.state}>
                {t(`states.${check.state}`)}
              </span>
            </div>
            <p>{t(`checks.${check.key}.why`)}</p>
            {check.state === "failed" && check.link ? <Link href={check.link}>{t("fix")}</Link> : null}
          </li>
        ))}
      </ul>
    </>
  );
}
