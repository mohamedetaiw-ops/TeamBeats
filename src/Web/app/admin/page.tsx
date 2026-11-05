import { getTranslations } from "next-intl/server";

export default async function AdminHome() {
  const t = await getTranslations("admin");
  return (
    <>
      <h1>{t("title")}</h1>
      <p>{t("overview")}</p>
    </>
  );
}
