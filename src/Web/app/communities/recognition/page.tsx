import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import {
  recognitionHref,
  type MyPoints,
  type PointsSettingsView,
  type RecognitionCatalogue,
  type RecognitionOptions,
  type RecognitionPage,
  type RecognitionPreference,
} from "@/lib/engagement";
import { Header } from "@/components/shell/Header";
import { PageHeader } from "@/components/ui/PageHeader";
import { StateView } from "@/components/ui/StateView";
import { RecognitionWall } from "@/features/engagement/RecognitionWall";

/** The recognition wall (spec 023 US4). The API decides the badges offered, whether points show and who manages settings. */
export default async function RecognitionPageRoute() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("engagement");
  const [options, wall, preference] = await Promise.all([
    apiGet<RecognitionOptions>("/api/v1/engagement/recognition/options"),
    apiGet<RecognitionPage>("/api/v1/engagement/recognitions"),
    apiGet<RecognitionPreference>("/api/v1/engagement/recognition/preferences"),
  ]);
  const header = (
    <Header
      me={me.data}
      breadcrumbs={[
        { label: t("pageTitle"), href: "/communities" },
        { label: t("recognition.pageTitle"), href: recognitionHref },
      ]}
    />
  );
  if (!options.ok || !wall.ok || !preference.ok) {
    return (
      <>
        {header}
        <main id="main" className="page">
          <PageHeader title={t("recognition.pageTitle")} icon="star" />
          <StateView kind={!options.ok && options.status === 403 ? "unsupported" : "error"} title={t("recognition.unavailableTitle")} message={t("recognition.unavailable")} />
        </main>
      </>
    );
  }
  const [points, catalogue, pointsSettings] = await Promise.all([
    options.data.pointsOn ? apiGet<MyPoints>("/api/v1/engagement/points/me") : Promise.resolve(null),
    options.data.canManage ? apiGet<RecognitionCatalogue>("/api/v1/engagement/settings/recognition") : Promise.resolve(null),
    options.data.canManage ? apiGet<PointsSettingsView>("/api/v1/engagement/settings/points") : Promise.resolve(null),
  ]);
  return (
    <>
      {header}
      <main id="main" className="page">
        <PageHeader title={t("recognition.pageTitle")} description={t("recognition.pageIntro")} icon="star" />
        <RecognitionWall
          locales={me.data.tenant.enabledLocales}
          initial={{
            options: options.data,
            wall: wall.data,
            preference: preference.data,
            points: points?.ok ? points.data : null,
            catalogue: catalogue?.ok ? catalogue.data : null,
            pointsSettings: pointsSettings?.ok ? pointsSettings.data : null,
          }}
        />
      </main>
    </>
  );
}
