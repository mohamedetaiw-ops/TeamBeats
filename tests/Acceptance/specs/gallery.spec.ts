import { request, type APIRequestContext } from "@playwright/test";
import { expect, expectAccessible, signInWithDevForm, test } from "../support/fixtures";
import { msg, type LaunchLocale } from "../support/messages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";

async function signedIn(tenant: TestTenant, email: string): Promise<APIRequestContext> {
  const host = `${tenant.key}.${stack.baseDomain}`;
  const anonymous = await request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host } });
  const response = await anonymous.post("/dev/signin", { data: { email } });
  const token = /tb_session=([^;]+)/.exec(response.headers()["set-cookie"] ?? "")?.[1];
  await anonymous.dispose();
  if (!response.ok() || !token) throw new Error(`Sign-in failed with ${response.status()}.`);
  return request.newContext({ baseURL: stack.apiOrigin, extraHTTPHeaders: { "x-forwarded-host": host, cookie: `tb_session=${token}` } });
}

/** A one pixel PNG. */
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "base64");

/**
 * Spec 023 T014 (photo galleries): Layla makes an album in a community, uploads a photo with a caption, which shows
 * after its scan and safety check, and tags the administrator; the administrator opens the album in their own browser
 * and removes their tag. Approval, held photos, metadata removal and the limits are covered by GalleryTests and
 * PhotoFilesTests. In English and Arabic, with axe. Names are fictional.
 */
for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`gallery in ${locale}`, () => {
    test.describe.configure({ mode: "serial" });
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });
    let tenant: TestTenant;
    let laylaEmail: string;
    let community: string;
    const m = (key: string, values: Record<string, string> = {}) => msg(locale, key, values);
    const albumTitle = locale === "ar" ? "رحلة الفريق" : "Team trip";
    const caption = locale === "ar" ? "على الشاطئ" : "At the beach";

    test.beforeAll(async () => {
      tenant = await createTenant(`gl-${locale}`, locale, "workplace", ["layla"]);
      laylaEmail = `layla@${tenant.key}.example`;
      const admin = await signedIn(tenant, tenant.adminEmail);
      const created = await admin.post("/api/v1/engagement/communities", { data: { names: { en: "Travel club", ar: "نادي السفر" }, kind: "open" } });
      expect(created.status(), await created.text()).toBe(201);
      community = ((await created.json()) as { id: string }).id;
      const layla = await signedIn(tenant, laylaEmail);
      const joined = await layla.post(`/api/v1/engagement/communities/${community}:join`);
      expect(joined.ok(), await joined.text()).toBe(true);
      await layla.dispose();
      await admin.dispose();
    });

    test("a member makes an album, uploads and tags a photo, and the person tagged removes their tag", async ({ page, browser }, testInfo) => {
      await signInWithDevForm(page, tenant.key, laylaEmail, locale);
      await page.goto(`${hostOrigin(tenant.key)}/communities/${community}`);
      await page.getByRole("link", { name: m("engagement.gallery.link"), exact: true }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(m("engagement.gallery.pageTitle"));

      const create = page.getByRole("region", { name: m("engagement.gallery.createTitle") });
      await create.getByLabel(m("engagement.gallery.albumTitle")).fill(albumTitle);
      await expectAccessible(page, testInfo, `gallery-albums-${locale}`);
      await create.getByRole("button", { name: m("engagement.gallery.create") }).click();
      const albums = page.getByRole("list", { name: m("engagement.gallery.albumsTitle") });
      await expect(albums.getByRole("heading", { name: albumTitle })).toBeVisible();
      await albums.getByRole("link", { name: m("engagement.gallery.openAlbum") }).click();
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(albumTitle);

      await page.getByLabel(m("engagement.gallery.caption")).fill(caption);
      await page.getByLabel(m("engagement.gallery.choosePhotos")).setInputFiles({ name: "beach.png", mimeType: "image/png", buffer: png });
      const photos = page.getByRole("list", { name: m("engagement.gallery.photosTitle") });
      const photo = photos.getByRole("article", { name: caption });
      await expect(photo.getByRole("img", { name: caption })).toBeVisible();

      await photo.getByRole("button", { name: m("engagement.gallery.tagPeople") }).click();
      const form = photo.getByRole("form", { name: m("engagement.gallery.tagPeople") });
      await form.getByLabel(m("services.person.search")).fill("admin");
      await form.getByRole("button", { name: m("services.person.find") }).click();
      await form.getByRole("list", { name: m("services.person.results") }).getByRole("button").first().click();
      await expectAccessible(page, testInfo, `gallery-album-${locale}`);
      await form.getByRole("button", { name: m("engagement.gallery.saveTags") }).click();
      await expect(photo.getByText(m("engagement.gallery.inPhoto"))).toBeVisible();

      const otherContext = await browser.newContext({ locale: locale === "ar" ? "ar-AE" : "en-US" });
      const other = await otherContext.newPage();
      await signInWithDevForm(other, tenant.key, tenant.adminEmail, locale);
      await other.goto(page.url());
      const tagged = other.getByRole("list", { name: m("engagement.gallery.photosTitle") }).getByRole("article", { name: caption });
      await expect(tagged.getByText(m("engagement.gallery.inPhoto"))).toBeVisible();
      await tagged.getByRole("button", { name: m("engagement.gallery.removeMyTag") }).click();
      await expect(tagged.getByText(m("engagement.gallery.inPhoto"))).toHaveCount(0);
      await expectAccessible(other, testInfo, `gallery-untagged-${locale}`);
      await otherContext.close();
    });
  });
}
