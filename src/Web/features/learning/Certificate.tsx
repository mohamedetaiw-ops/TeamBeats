"use client";

import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Alert } from "@/components/ui/Alert";
import { Button } from "@/components/ui/Button";
import { FormField } from "@/components/ui/FormField";
import { Icon } from "@/components/ui/Icon";
import { certificatePdf, localText, type CertificateView } from "@/lib/learning";
import "./learning.css";

/** A certificate of completion (spec 028 FR-010): who finished which course and version, when, and its ID to verify. */
export function Certificate({ certificate, download = true }: { certificate: CertificateView; download?: boolean }) {
  const t = useTranslations("learning");
  const locale = useLocale();
  const format = useFormatter();
  const date = (value: string) => format.dateTime(new Date(value), { dateStyle: "long" });
  return (
    <div className="learning">
      <section className="learning-certificate" aria-labelledby="certificate-title">
        <Icon name="star" size={32} />
        <h2 id="certificate-title">{t("certificate.heading")}</h2>
        <p>{t("certificate.certifies")}</p>
        <p>
          <strong>
            <bdi>{certificate.learner.displayName}</bdi>
          </strong>
        </p>
        <p>{t("certificate.completedCourse")}</p>
        <p>
          <strong>
            <bdi>{localText(certificate.courseTitles, locale)}</bdi>
          </strong>
        </p>
        <p className="learning-meta">{t("certificate.version", { version: certificate.version })}</p>
        <p className="learning-meta">{t("certificate.issued", { date: date(certificate.issuedAt) })}</p>
        {certificate.expiresAt ? <p className="learning-meta">{t("certificate.expires", { date: date(certificate.expiresAt) })}</p> : null}
        <p className="learning-meta">
          {t("certificate.code")} <span className="learning-code">{certificate.code}</span>
        </p>
      </section>
      {download ? (
        <div className="learning-row">
          <a className="button button-primary" data-variant="primary" data-icon href={certificatePdf(certificate.id)} download>
            <Icon name="download" size={18} />
            <span>{t("certificate.download")}</span>
          </a>
        </div>
      ) : null}
    </div>
  );
}

/** Checks a certificate ID as printed (spec 028 FR-010). Spaces, dashes and case do not matter; the API decides who may check. */
export function VerifyForm({ code, found }: { code: string; found: CertificateView | null | "denied" }) {
  const t = useTranslations("learning");
  return (
    <div className="learning">
      <form className="learning-row" method="get">
        <FormField label={t("verify.code")} name="code" defaultValue={code} maxLength={20} hint={t("verify.codeHint")} dir="ltr" autoComplete="off" />
        <Button type="submit" icon="search">
          {t("verify.check")}
        </Button>
      </form>
      {code && found === null ? (
        <Alert tone="warning" title={t("verify.notFoundTitle")} live>
          {t("verify.notFound")}
        </Alert>
      ) : null}
      {found === "denied" ? (
        <Alert tone="danger" title={t("verify.deniedTitle")} live>
          {t("verify.denied")}
        </Alert>
      ) : null}
      {found && found !== "denied" ? (
        <>
          <Alert tone="success" title={t("verify.validTitle")} live>
            {t("verify.valid")}
          </Alert>
          <Certificate certificate={found} download={false} />
        </>
      ) : null}
    </div>
  );
}
