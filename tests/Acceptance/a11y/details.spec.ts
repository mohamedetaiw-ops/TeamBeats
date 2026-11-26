import { readFileSync } from "node:fs";
import type { APIRequestContext, Page } from "@playwright/test";
import { expect, signInWithDevForm, test } from "../support/fixtures";
import { chooseDesign, chooseMode, expectConformant, settle } from "../support/a11y";
import { seedHome } from "../support/home";
import type { LaunchLocale } from "../support/messages";
import { tenantApi, tenantApiAs } from "../support/pages";
import { createTenant, type TestTenant } from "../support/platform";
import { hostOrigin, stack } from "../support/stack";
import { routes } from "./routes";

/**
 * Detail routes with real records (spec 038 T008, section 7: "a route that needs data to show its real content is
 * checked with seeded data, not only in its empty state"). Each language gets a tenant filled with the demo data
 * package (docs/testing/demo-data.md) and a few records of the modules the package does not cover yet. Every detail
 * route of the inventory is then opened on a real record, found the way a person finds it: by following a link on the
 * page that lists it. The design, mode and width rotate over the routes as for the other routes of the matrix.
 * A detail route with a reason in the inventory is checked by its feature suite instead; any other detail route that
 * finds no record fails here, so a new detail page cannot slip past the gate.
 */
test.skip(stack.target === "remote", "The demo data endpoint exists in the development host only.");

const designs = ["beats", "atlas", "pulse", "studio"] as const;
const modes = ["light", "dark"] as const;
const widths = [
  { name: "desktop", width: 1280, height: 900 },
  { name: "phone", width: 390, height: 844 },
] as const;

/**
 * Where each record is found: the page that links to it. A list may itself be a detail route, found by an earlier
 * entry, so the order matters.
 */
type Finder = { route: string; list: string | ((api: APIRequestContext) => Promise<string[]>) };
const finders: Finder[] = [
  { route: "/kb/[id]", list: "/kb" },
  { route: "/content/items/[id]", list: "/content/items" },
  { route: "/content/sites/[id]", list: "/content/sites" },
  { route: "/services/[id]", list: "/services" },
  { route: "/admin/services/[id]", list: "/admin/services" },
  { route: "/analytics/services/[id]", list: "/analytics/services" },
  { route: "/requests/[id]", list: "/requests" },
  { route: "/documents/libraries/[id]", list: "/documents" },
  // Documents sit in folders, which the library opens in place; each folder has its own address.
  { route: "/documents/[id]", list: documentFolders },
  { route: "/policies/[id]", list: "/policies" },
  { route: "/policies/[id]/versions/[number]", list: "/policies/[id]" },
  { route: "/spaces/[id]", list: "/spaces" },
  { route: "/spaces/[id]/tasks/[taskId]", list: "/spaces/[id]/tasks" },
  { route: "/people/[id]", list: "/people" },
  { route: "/admin/users/[id]", list: "/admin/users" },
  { route: "/admin/pages/[id]", list: "/admin/pages" },
  { route: "/communities/[id]", list: "/communities" },
  { route: "/communities/[id]/posts/[postId]", list: "/communities/[id]" },
  { route: "/communities/hashtags/[tag]", list: "/communities/[id]" },
  { route: "/communities/[id]/albums/[albumId]", list: "/communities/[id]/albums" },
  { route: "/communities/[id]/rooms/[roomId]", list: "/communities/[id]/rooms" },
  { route: "/communities/surveys/[id]", list: "/communities/surveys" },
  { route: "/learning/courses/[id]", list: "/learning/catalogue" },
  { route: "/learning/manage/courses/[id]", list: "/learning/manage" },
  { route: "/learning/certificates/[id]", list: "/learning" },
  { route: "/learning/reports/[id]", list: "/learning/reports" },
  { route: "/booking/manage/sites/[id]", list: "/booking/manage" },
  { route: "/admin/agents/[id]", list: "/admin/agents" },
];

/** Routes keyed by the same record as another route: the id of the first is the id of the second. */
const sameRecord: Record<string, string> = {
  "/content/read/[id]": "/content/items/[id]",
  "/preview/pages/[id]": "/admin/pages/[id]",
  "/page-approvals/[id]": "/admin/pages/[id]",
  "/attendance/people/[userId]": "/admin/users/[id]",
  "/policies/compliance/people/[userId]": "/admin/users/[id]",
  "/admin/evaluation/agents/[id]": "/admin/agents/[id]",
};

const idPattern = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";

function segmentPattern(name: string): string {
  if (name === "number" || name === "n") return "(\\d+)";
  if (name === "tag" || name === "key" || name === "site" || name === "code" || name === "token") return "([^/?#]+)";
  return `(${idPattern})`;
}

/** Path values found so far, by finder route: for example "/kb/[id]" to "/kb/0193...". */
type Found = Map<string, string>;

/** The concrete path of a route, from the longest finder (or same record route) it starts with; null if none. */
function resolve(route: string, found: Found): string | null {
  if (!route.includes("[")) return route;
  let best: { template: string; path: string } | null = null;
  const candidates = [...found.entries()].map(([template, path]) => ({ template, path }));
  for (const [alias, target] of Object.entries(sameRecord)) {
    const path = found.get(target);
    if (path) candidates.push({ template: alias, path: alias.replace(/\[[^\]]+\]/, path.split("/").pop()!) });
  }
  for (const c of candidates) {
    if ((route === c.template || route.startsWith(`${c.template}/`)) && (!best || c.template.length > best.template.length)) best = c;
  }
  if (!best) return null;
  const path = best.path + route.slice(best.template.length);
  return path.includes("[") ? null : path;
}

/** The address of every folder of every library, for the documents in them. */
async function documentFolders(api: APIRequestContext): Promise<string[]> {
  const libraries = (await (await api.get("/api/v1/documents/libraries")).json()) as { id: string }[];
  const paths: string[] = [];
  for (const library of libraries) {
    const folders = (await (await api.get(`/api/v1/documents/libraries/${library.id}/folders`)).json()) as { id: string }[];
    paths.push(...folders.map((f) => `/documents/libraries/${library.id}?folder=${f.id}`));
  }
  return paths.slice(0, 6);
}

/** Every list page a finder may use: when the list is itself a record, each record found for it, in order. */
function listPaths(list: string, found: Found, all: Map<string, string[]>): string[] {
  if (!list.includes("[")) return [list];
  const template = [...found.keys()].filter((t) => list.startsWith(`${t}/`) || list === t).sort((x, y) => y.length - x.length)[0];
  if (!template) return [];
  return (all.get(template) ?? [found.get(template)!]).slice(0, 6).map((path) => path + list.slice(template.length));
}

/**
 * Follows the finders in order, opening each list page and taking the first link that matches its route. When a list
 * is a record (a library, a space), the next record of that kind is tried while the first shows nothing to follow.
 */
async function findRecords(page: Page, api: APIRequestContext, origin: string, found: Found, misses: Record<string, string[]>): Promise<void> {
  const all = new Map<string, string[]>();
  for (const { route, list } of finders) {
    const lists = typeof list === "string" ? listPaths(list, found, all) : await list(api);
    for (const listPath of lists) {
      await page.goto(`${origin}${listPath}`);
      await page.waitForLoadState("networkidle", { timeout: 15_000 }).catch(() => undefined);
      // The route with the list's own record filled in, the last unknown segment captured.
      const known = typeof list === "string" ? resolveFrom(route, list, listPath) : route;
      const source = known
        .split("/")
        .map((segment) => (segment.startsWith("[") ? segmentPattern(segment.slice(1, -1)) : segment.replace(/[.*+?^${}()|\\]/g, "\\$&")))
        .join("/");
      const pattern = new RegExp(`^${source}$`);
      const hrefs = await page.locator("main a[href]").evaluateAll((anchors) => anchors.map((a) => a.getAttribute("href") ?? ""));
      const matches = [...new Set(hrefs.map((href) => href.split(/[?#]/)[0]!).filter((href) => pattern.test(href) && !/\/(new|manage)$/.test(href)))];
      if (matches.length > 0) {
        found.set(route, matches[0]!);
        all.set(route, matches);
        delete misses[route];
        break;
      }
      misses[route] = [...(misses[route] ?? []), `${listPath}: ${[...new Set(hrefs)].join(" ")}`];
    }
  }
}

/** The route with the parameters the list page shares with it filled in from the list's concrete path. */
function resolveFrom(route: string, list: string, listPath: string): string {
  const template = list.split("/");
  const concrete = listPath.split("/");
  return route
    .split("/")
    .map((segment, i) => (segment.startsWith("[") && template[i] === segment ? concrete[i]! : segment))
    .join("/");
}

/** The template the staging home is built on, which a tenant template starts from. */
const homeTemplate = (JSON.parse(readFileSync(new URL("../../../src/Api.DevHost/seeds/staging-home.json", import.meta.url), "utf8")) as { template: string }).template;

const paragraph = (text: string) => ({ blocks: [{ type: "paragraph", children: [{ text }] }] });
const both = (en: string, ar: string) => ({ en, ar });

async function ok(response: Awaited<ReturnType<APIRequestContext["post"]>>): Promise<Record<string, unknown>> {
  expect(response.ok(), `${response.url()}: ${await response.text()}`).toBe(true);
  return (await response.json().catch(() => ({}))) as Record<string, unknown>;
}

/**
 * Records of modules the demo data package does not fill yet: a community with a post, an album and a poll, a pulse
 * survey, a published course, a booking site and an installed agent, all in one org unit of the administrator.
 */
async function seedExtras(api: APIRequestContext, found: Found): Promise<void> {
  const unit = await ok(await api.post("/api/v1/org/units", {
    data: { parentId: null, typeKey: "department", code: "PEOPLE", names: both("People services", "خدمات الموظفين") },
  }));
  const unitId = (unit.unit as { id: string }).id;
  const me = (await (await api.get("/api/v1/me")).json()) as { id: string };
  await ok(await api.post(`/api/v1/org/units/${unitId}/memberships`, { data: { userId: me.id, type: "primary" } }));

  const community = await ok(await api.post("/api/v1/engagement/communities", {
    data: { names: both("Photography club", "نادي التصوير"), descriptions: both("Photos from our walks.", "صور من جولاتنا."), kind: "open" },
  }));
  const communityId = community.id as string;
  await ok(await api.post(`/api/v1/engagement/communities/${communityId}/posts`, {
    data: { body: paragraph("Golden light at the harbour this morning #golden_hour") },
  }));
  await ok(await api.post(`/api/v1/engagement/communities/${communityId}/albums`, {
    data: { title: "Harbour walk", description: "Our first walk of the season.", requiresApproval: false },
  }));
  await ok(await api.post(`/api/v1/engagement/communities/${communityId}/rooms`, {
    data: { names: both("Walk planning", "تخطيط الجولات") },
  }));
  await ok(await api.post(`/api/v1/engagement/communities/${communityId}/polls`, {
    data: { question: "Where should we walk next?", options: ["The old town", "The corniche"], multiple: false, anonymous: false, allowChange: true, results: "after_vote", closesAt: new Date(Date.now() + 7 * 86_400_000).toISOString() },
  }));
  const survey = await ok(await api.post("/api/v1/engagement/surveys", {
    data: {
      orgUnitId: unitId, title: "How is your week going?", intro: "A short pulse check.", anonymous: true, threshold: null,
      closesAt: new Date(Date.now() + 7 * 86_400_000).toISOString(),
      questions: [{ kind: "scale", prompt: "How would you rate your workload?", required: true, options: [] }],
    },
  }));
  await ok(await api.post(`/api/v1/engagement/surveys/${survey.id as string}:publish`));

  const course = await ok(await api.post("/api/v1/learning/manage/courses", {
    data: {
      orgUnitId: unitId, category: "Security", audience: null, openEnrolment: true,
      definition: {
        titles: both("Information security basics", "أساسيات أمن المعلومات"),
        summaries: both("Passwords and phishing.", "كلمات المرور والتصيد."),
        minutes: 10,
        lessons: [{
          key: "check", kind: "quiz", titles: both("Check", "اختبار"),
          quiz: {
            passMark: 50, attempts: 2,
            questions: [{
              key: "q1", kind: "single", prompts: both("Which password is strongest?", "أي كلمة مرور أقوى؟"),
              options: [
                { key: "a", labels: both("Summer2026", "صيف2026"), correct: false },
                { key: "b", labels: both("A long passphrase", "عبارة مرور طويلة"), correct: true },
              ],
            }],
          },
        }],
      },
    },
  }));
  await ok(await api.post(`/api/v1/learning/manage/courses/${course.id as string}:publish`, { data: { revision: course.revision, requiresRetraining: false } }));
  const path = await ok(await api.post("/api/v1/learning/manage/paths", {
    data: {
      orgUnitId: unitId, titles: both("New starter path", "مسار الموظف الجديد"), summaries: both("Your first month.", "شهرك الأول."), audience: null,
      courses: [{ courseId: course.id, requiresPrevious: false }],
    },
  }));
  await ok(await api.post(`/api/v1/learning/manage/paths/${path.id as string}:publish`));
  found.set("/learning/manage/paths/[id]", `/learning/manage/paths/${path.id as string}`);
  found.set("/learning/paths/[id]", `/learning/paths/${path.id as string}`);
  // The administrator takes the course and passes, for a certificate.
  await ok(await api.post(`/api/v1/learning/courses/${course.id as string}:enrol`));
  await ok(await api.post(`/api/v1/learning/courses/${course.id as string}/lessons/check/attempts`, { data: { answers: { q1: ["b"] } } }));

  await ok(await api.post("/api/v1/booking/manage/sites", {
    data: { names: both("Head office", "المكتب الرئيسي"), timeZone: "Asia/Dubai", opensAt: "08:00:00", closesAt: "18:00:00" },
  }));
  await ok(await api.post("/api/v1/agents", { data: { templateId: "enterprise_knowledge", orgUnitId: unitId } }));
  const app = await ok(await api.post("/api/v1/api-access/apps", {
    data: { name: "Field notes", kind: "delegated_public", publisher: "Example IT", scopes: ["profile:read"], redirectUris: ["https://notes.example/callback"] },
  }));
  found.set("/admin/api/apps/[id]", `/admin/api/apps/${app.id as string}`);
  const template = await ok(await api.post("/api/v1/experience/templates", {
    data: { id: "a11y-landing", basedOn: homeTemplate, names: both("Department landing", "صفحة القسم"), descriptions: both("A landing page for a department.", "صفحة رئيسية لقسم.") },
  }));
  found.set("/admin/templates/[id]", `/admin/templates/${(template.id as string | undefined) ?? "a11y-landing"}`);
  const campaign = await ok(await api.post("/api/v1/content/campaigns", { data: { name: "Benefits month", siteId: null } }));
  found.set("/content/campaigns/[id]", `/content/campaigns/${campaign.id as string}`);
  // Campaign analytics (spec 021 T011) open on a campaign before it is sent.
  found.set("/analytics/campaigns/[id]", `/analytics/campaigns/${campaign.id as string}`);
  // A unit dashboard (spec 021 T014) for the seeded unit.
  found.set("/analytics/scopes/unit/[id]", `/analytics/scopes/unit/${unitId}`);
  // An export (spec 021 T016): the report page opens while it is still being made.
  const report = await ok(await api.post("/api/v1/analytics/exports", {
    data: { definition: { dashboard: "overview", metric: "active_people" }, format: "xlsx", name: "Weekly activity" },
  }));
  found.set("/analytics/reports/[id]", `/analytics/reports/${report.id as string}`);
}

const detailRoutes = routes.filter((r) => r.tier === "detail" && !r.reason).map((r) => r.path);

for (const locale of ["en", "ar"] as const satisfies readonly LaunchLocale[]) {
  test.describe(`detail routes in ${locale}`, () => {
    test.use({ locale: locale === "ar" ? "ar-AE" : "en-US" });

    test("every detail route on a seeded record, in its rotated design, mode and width", async ({ page }, testInfo) => {
      test.setTimeout(30 * 60_000);
      const tenant: TestTenant = await createTenant(`a11y-detail-${locale}`, locale, "enterprise_gov", ["approver"]);
      const found: Found = new Map();
      const home = await seedHome(tenant);
      found.set("/p/[...slug]", `/p/${home.slug}`);
      // A second person approves the policies the administrator writes; nobody approves their own.
      const approverEmail = `approver@${tenant.key}.example`;
      await (await tenantApiAs(tenant, approverEmail)).dispose();
      const origin = hostOrigin(tenant.key);
      const misses: Record<string, string[]> = {};
      const api = await tenantApi(tenant);
      try {
        const seeded = await api.post("/api/v1/dev/demo-data", { data: { people: { youssef: approverEmail, salma: approverEmail } }, timeout: 600_000 });
        expect(seeded.ok(), await seeded.text()).toBe(true);
        await testInfo.attach("demo-data.json", { body: await seeded.text(), contentType: "application/json" });
        await seedExtras(api, found);
        // A campaign page has no link of its own (the campaigns page shows it in place), so it is found through the API.
        const policies = (await (await api.get("/api/v1/policies")).json()) as { items?: { id: string }[] } | { id: string }[];
        for (const policy of Array.isArray(policies) ? policies : (policies.items ?? [])) {
          const campaigns = (await (await api.get(`/api/v1/policies/${policy.id}/campaigns`)).json()) as { id: string }[] | { items: { id: string }[] };
          const first = (Array.isArray(campaigns) ? campaigns : campaigns.items)[0];
          if (first) {
            found.set("/policies/[id]/campaigns/[campaignId]", `/policies/${policy.id}/campaigns/${first.id}`);
            break;
          }
        }
        await signInWithDevForm(page, tenant.key, tenant.adminEmail, locale);
        await findRecords(page, api, origin, found, misses);
      } finally {
        await api.dispose();
      }
      await testInfo.attach("records-not-found.json", { body: JSON.stringify(misses, null, 2), contentType: "application/json" });
      const resolved = detailRoutes.map((route) => ({ route, path: resolve(route, found) }));
      expect
        .soft(resolved.filter((r) => !r.path).map((r) => r.route), "detail routes with no seeded record (seed one, or give the route a reason in routes.ts)")
        .toEqual([]);

      const rotation = resolved
        .filter((r): r is { route: string; path: string } => !!r.path)
        .map((r, i) => ({
          ...r,
          // The other language starts half way round, so each route is seen in two designs.
          design: designs[(i + (locale === "ar" ? 2 : 0)) % designs.length]!,
          mode: modes[Math.floor(i / designs.length) % modes.length]!,
          width: widths[Math.floor(i / (designs.length * modes.length)) % widths.length]!,
        }));
      for (const design of designs) {
        await chooseDesign(tenant, design);
        for (const mode of modes) {
          const mine = rotation.filter((r) => r.design === design && r.mode === mode);
          if (mine.length === 0) continue;
          await chooseMode(tenant, mode);
          for (const { route, path, width } of mine) {
            await page.setViewportSize(width);
            await page.goto(`${origin}${path}`);
            await expect(page.locator("html")).toHaveAttribute("data-design", design);
            await settle(page);
            await expectConformant(page, testInfo, `${design}-${mode}-${locale}-${width.name}${route.replace(/[/[\]]+/g, "-")}`);
          }
        }
      }
    });
  });
}
