import Link from "next/link";
import { notFound, redirect } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { apiGet, type Me } from "@/lib/api";
import { readHref, type ReaderView, type TeamMemberStatus } from "@/lib/content/types";
import { Header } from "@/components/shell/Header";
import { StateView } from "@/components/ui/StateView";
import "@/features/content/content.css";

/** A manager's view of which direct reports have confirmed a critical announcement (spec 009 US2 scenario 5). */
export default async function TeamAcknowledgementsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const [item, team] = await Promise.all([
    apiGet<ReaderView>(`/api/v1/content/items/${encodeURIComponent(id)}/view`),
    apiGet<TeamMemberStatus[]>(`/api/v1/content/items/${encodeURIComponent(id)}/acknowledgements/team`),
  ]);
  if (!item.ok || !team.ok) {
    notFound();
  }
  const t = await getTranslations("content.acks");
  const locale = await getLocale();
  const format = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
  return (
    <>
      <Header me={me.data} breadcrumbs={[{ label: t("teamTitle"), href: `/content/items/${encodeURIComponent(id)}/team` }]} />
      <main id="main" className="page content-manage">
        <h1>{t("teamHeading", { title: item.data.title })}</h1>
        <p>
          <Link href={readHref(id)}>{t("openItem")}</Link>
        </p>
        {team.data.length === 0 ? (
          <StateView kind="empty" title={t("noTeam")} message="" />
        ) : (
          <table className="content-table">
            <caption className="visually-hidden">{t("teamTitle")}</caption>
            <thead>
              <tr>
                <th scope="col">{t("person")}</th>
                <th scope="col">{t("state")}</th>
                <th scope="col">{t("when")}</th>
              </tr>
            </thead>
            <tbody>
              {team.data.map((m) => (
                <tr key={m.person.id}>
                  <th scope="row">
                    <bdi>{m.person.displayName}</bdi>
                  </th>
                  <td>{m.confirmed ? t("yes") : t("no")}</td>
                  <td>{m.at ? <time dateTime={m.at}>{format.format(new Date(m.at))}</time> : null}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </main>
    </>
  );
}
