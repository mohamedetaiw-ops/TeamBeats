import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { recordsHref, type DispositionItem, type DispositionRecordView, type HoldView, type LabelView, type RecordsLibraryView } from "@/lib/records/types";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { ApplyPanel, DispositionPanel, HoldsPanel, LabelsPanel } from "@/features/records/RecordsAdmin";

/**
 * Records management (spec 011 R2 US6 and US7) for records managers: retention labels and where they apply, the
 * disposition review queue with its records, and legal holds. The API answers 403 to anyone else and for packages
 * without retention; legal holds have their own capability, so their section is left out when the API refuses it.
 */
export default async function RecordsPage() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [labels, libraries, queue, records, holds] = await Promise.all([
    apiGet<LabelView[]>("/api/v1/records/labels"),
    apiGet<RecordsLibraryView[]>("/api/v1/records/libraries"),
    apiGet<DispositionItem[]>("/api/v1/records/disposition"),
    apiGet<DispositionRecordView[]>("/api/v1/records/dispositions"),
    apiGet<HoldView[]>("/api/v1/records/holds"),
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
                <DispositionPanel queue={queue.data} records={records.data} />
              </>
            ) : null}
            {holds.ok ? <HoldsPanel holds={holds.data} libraries={libraries.ok ? libraries.data : []} /> : null}
          </>
        )}
      </main>
    </>
  );
}
