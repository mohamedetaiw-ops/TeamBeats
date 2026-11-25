import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  recordsHref,
  type DispositionCertificateView,
  type DispositionItem,
  type DispositionRecordView,
  type HoldView,
  type LabelView,
  type RecordItem,
  type RecordsLibraryView,
} from "@/lib/records/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ApplyPanel, CertificatesPanel, DispositionPanel, HoldsPanel, LabelsPanel, RecordsPanel } from "@/features/records/RecordsAdmin";

/**
 * Records management (spec 011 R2 US6 and US7, R3) for records managers: retention labels and where they apply, the
 * documents declared records, the disposition review queue with its records and certificates, and legal holds. The API answers 403 to anyone else and for packages
 * without retention; legal holds have their own capability, so their section is left out when the API refuses it.
 */
export default async function RecordsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [labels, libraries, queue, records, holds, declared, certificates] = await Promise.all([
    apiGet<LabelView[]>("/api/v1/records/labels"),
    apiGet<RecordsLibraryView[]>("/api/v1/records/libraries"),
    apiGet<DispositionItem[]>("/api/v1/records/disposition"),
    apiGet<DispositionRecordView[]>("/api/v1/records/dispositions"),
    apiGet<HoldView[]>("/api/v1/records/holds"),
    apiGet<RecordItem[]>("/api/v1/records/records"),
    apiGet<DispositionCertificateView[]>("/api/v1/records/certificates"),
  ]);
  const t = await getTranslations("records");
  const retention = labels.ok && libraries.ok && queue.ok && records.ok;
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("title"), href: recordsHref }]} />
      <main id="main" className="page records-page">
        <PageHeader title={t("title")} description={t("intro")} icon="files" />
        {!retention && !holds.ok ? (
          <StateView kind="denied" title={t("denied")} message="" />
        ) : (
          <>
            {retention ? (
              <>
                <LabelsPanel labels={labels.data} />
                <ApplyPanel libraries={libraries.data} labels={labels.data} />
                {declared.ok ? <RecordsPanel records={declared.data} /> : null}
                <DispositionPanel queue={queue.data} records={records.data} />
                {certificates.ok ? <CertificatesPanel certificates={certificates.data} /> : null}
              </>
            ) : null}
            {holds.ok ? <HoldsPanel holds={holds.data} libraries={libraries.ok ? libraries.data : []} /> : null}
          </>
        )}
      </main>
    </>
  );
}
