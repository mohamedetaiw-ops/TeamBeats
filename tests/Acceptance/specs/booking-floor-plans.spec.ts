import { deflateSync } from "node:zlib";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { expectDetailConformant } from "../support/a11y";
import { msg, type LaunchLocale } from "../support/messages";
import { tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin } from "../support/stack";

/** A Tuesday at least two days ahead, so the day is a working day within the default 14 days. */
function nextTuesday(): string {
  const day = new Date(Date.now() + 2 * 86_400_000);
  while (day.getUTCDay() !== 2) day.setUTCDate(day.getUTCDate() + 1);
  return day.toISOString().slice(0, 10);
}

/** A plain light PNG of the given size, made here so no image file is checked in. */
function png(width: number, height: number): Buffer {
  const table = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (data: Buffer) => {
    let c = 0xffffffff;
    for (const b of data) c = table[(c ^ b) & 0xff]! ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type: string, data: Buffer) => {
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const out = Buffer.alloc(body.length + 8);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc(body), body.length + 4);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8);
  const row = Buffer.concat([Buffer.from([0]), Buffer.alloc(width * 3, 0xee)]);
  const raw = Buffer.concat(Array.from({ length: height }, () => row));
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", header), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

/**
 * Spec 031 R3 US7: the administrator uploads a plan for a floor and places a desk by choosing it and clicking the plan,
 * and a meeting room by typing its position; Layla opens the floor for a time, sees the desk free on the plan and books
 * it from the list. Image checks, positions, states, the entitlement and the tenant boundary are covered by
 * BookingFloorPlanTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`booking floor plans in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let floor = "";
    const m = (key: string, values: Record<string, string | number> = {}) => msg(locale, key, values as Record<string, string>);
    const desk = locale === "ar" ? "مكتب 2.01" : "Desk 2.01";
    const room = locale === "ar" ? "غرفة النخيل" : "Palm room";
    const floorName = locale === "ar" ? "الطابق الثاني" : "Second floor";

    test.beforeAll(async () => {
      tenant = await createTenant(`bkgfp-${locale}`, locale, "workplace_plus", ["layla"]);
      const api = await tenantApiAs(tenant, tenant.adminEmail);
      const ok = async (response: Awaited<ReturnType<typeof api.post>>) => {
        expect(response.ok(), await response.text()).toBeTruthy();
        return (await response.json()) as { id: string };
      };
      const names = (value: string) => ({ [locale]: value });
      const site = await ok(await api.post("/api/v1/booking/manage/sites", { data: { names: names(locale === "ar" ? "مكتب جدة" : "Jeddah office"), timeZone: "Asia/Riyadh", opensAt: "08:00:00", closesAt: "18:00:00" } }));
      floor = (await ok(await api.post(`/api/v1/booking/manage/sites/${site.id}/floors`, { data: { names: names(floorName), sort: 2 } }))).id;
      for (const [kind, name] of [["desk", desk], ["room", room]] as const) {
        await ok(await api.post("/api/v1/booking/manage/resources", { data: { siteId: site.id, floorId: floor, zoneId: null, kind, names: names(name), capacity: kind === "room" ? 6 : null, attributes: [] } }));
      }
      await api.dispose();
    });

    test("an administrator uploads the plan and places the desk and the room", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking/manage/floors/${floor}`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("booking.floorEditor.pageTitle"));
      const main = page.getByRole("main");
      await expect(main.getByText(m("booking.floorEditor.noPlanTitle"))).toBeVisible();
      await main.getByLabel(m("booking.floorEditor.file")).setInputFiles({ name: "plan.png", mimeType: "image/png", buffer: png(400, 250) });
      await main.getByRole("button", { name: m("booking.floorEditor.upload") }).click();
      await expect(main.getByText(m("booking.floorEditor.uploaded"))).toBeVisible();

      await main.getByRole("button", { name: desk, exact: true }).click();
      const plan = main.getByRole("img", { name: m("booking.floorEditor.planAlt", { floor: floorName }) });
      await plan.click({ position: { x: 100, y: 50 } });
      await expect(main.getByLabel(m("booking.floorEditor.x", { name: desk }))).not.toHaveValue("");
      await main.getByLabel(m("booking.floorEditor.x", { name: room })).fill("70");
      await main.getByLabel(m("booking.floorEditor.y", { name: room })).fill("55");
      await main.getByRole("button", { name: m("booking.floorEditor.save") }).click();
      await expect(main.getByText(m("booking.floorEditor.saved"))).toBeVisible();
      await expect(main.getByRole("button", { name: room, exact: true }).and(main.locator(".ui-planmap-pin"))).toBeVisible();
      await expectAccessible(page, testInfo, `booking-floor-editor-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/booking/manage/floors/[id]");
    });

    test("Layla sees the desk free on the plan and books it from the list", async ({ page }, testInfo) => {
      await signInWithDevForm(page, tenant.key, `layla@${tenant.key}.example`, locale);
      await page.goto(`${hostOrigin(tenant.key)}/booking/floors/${floor}?date=${nextTuesday()}&start=10:00&end=11:00`);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(floorName);
      const main = page.getByRole("main");
      const pin = main.getByRole("button", { name: m("booking.floor.pin", { name: desk, state: m("booking.floor.state.free") }) });
      await expect(pin).toBeVisible();
      await pin.click();
      await expect(main.getByText(m("booking.floor.selected", { name: desk }))).toBeVisible();
      await expectAccessible(page, testInfo, `booking-floor-${locale}`);
      await expectDetailConformant(page, testInfo, tenant, "/booking/floors/[id]");
      await main.getByRole("button", { name: m("booking.floor.book", { name: desk }) }).click();
      await expect(main.getByText(m("booking.floor.booked", { name: desk }))).toBeVisible();
      await expect(main.getByRole("button", { name: m("booking.floor.pin", { name: desk, state: m("booking.floor.state.busy") }) })).toBeVisible();
    });
  });
}
