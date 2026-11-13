import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import { listUnits } from "@/lib/experience/client";
import { pick } from "@/lib/services/types";
import type { ProcessModelView } from "@/lib/processes";
import { Modeler } from "@/features/processes/Modeler";

/** One process model (spec 024 FR-001). A model the person may not design or read is not found. */
export default async function ProcessPage({ params }: { params: Promise<{ key: string }> }) {
  const { key } = await params;
  const [model, units] = await Promise.all([apiGet<ProcessModelView>(`/api/v1/workflow/processes/${encodeURIComponent(key)}`), listUnits()]);
  if (!model.ok) {
    notFound();
  }
  const t = await getTranslations("processes.modeler");
  const locale = await getLocale();
  return (
    <>
      <p>
        <Link href="/admin/processes">{t("back")}</Link>
      </p>
      <h1>
        <bdi>{pick(model.data.names, locale, model.data.key)}</bdi>
      </h1>
      <Modeler processKey={model.data.key} initial={model.data} units={units.ok ? units.data : []} />
    </>
  );
}
