import Link from "next/link";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { apiGet, type Me, type NavigationSection } from "@/lib/api";
import { Header } from "@/components/shell/Header";

export default async function Home() {
  const me = await apiGet<Me>("/api/v1/me");
  if (!me.ok) {
    redirect("/signin");
  }
  const t = await getTranslations("home");
  const nav = await apiGet<NavigationSection[]>("/api/v1/admin/navigation");
  return (
    <>
      <Header me={me.data} />
      <main id="main" className="page">
        <h1>
          {t("title", { name: me.data.displayName })}
        </h1>
        <p>
          {t("subtitle", { tenant: me.data.tenant.key })}
        </p>
        {nav.ok && nav.data.length > 0 ? (
          <Link className="button" href="/admin">
            {t("openAdmin")}
          </Link>
        ) : null}
      </main>
    </>
  );
}
