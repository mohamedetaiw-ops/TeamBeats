import { getTranslations } from "next-intl/server";
import { apiGet } from "@/lib/api";
import type { KioskDeviceInfo } from "@/lib/kiosk";
import { KioskEnrol } from "@/features/kiosk/KioskEnrol";
import { KioskSignIn } from "@/features/kiosk/KioskSignIn";
import "@/features/kiosk/kiosk.css";

// The device cookie decides what this page shows, so it is read on every request.
export const dynamic = "force-dynamic";

/**
 * The start screen of a shared kiosk (spec 030 US4). An enrolled tablet offers badge and PIN sign-in; any other device
 * offers enrolment with the one-time code a device administrator created. Opening it ends whatever session was left.
 */
export default async function KioskPage() {
  const t = await getTranslations("kiosk");
  const device = await apiGet<KioskDeviceInfo>("/api/v1/kiosk/device");
  const enrolled = device.ok && device.data.enrolled;
  return (
    <main id="main" className="page narrow kiosk-start">
      <h1>{enrolled ? device.data.name || t("title") : t("title")}</h1>
      {enrolled ? <KioskSignIn device={device.data} /> : <KioskEnrol />}
    </main>
  );
}
