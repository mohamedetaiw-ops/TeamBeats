import { problemMessages } from "@/lib/notifications/types";

/** Learning and mandatory training (spec 028): the shapes the API returns. Who may take, build or report is decided there. */
export const learningApi = "/api/v1/learning";

export type Texts = Record<string, string>;

export type LessonKind = "page" | "document" | "video" | "quiz" | "package";
export type QuestionKind = "single" | "multiple" | "true_false";
export type LearnerStateKind = "none" | "open" | "completed" | "retraining";
export type AssignmentSource = "self" | "rule" | "manager" | "onboarding" | "policy";

export type LearningRights = { take: boolean; manage: boolean; report: boolean; team: boolean; packages?: boolean };

/** Where a package plays: a sandboxed content address holding a launch token (spec 028 US7). */
export type LaunchView = { url: string; kind: "scorm12" | "xapi"; expiresAt: string };

/** An imported SCORM 1.2 or xAPI package. */
export type PackageView = { id: string; courseId: string; kind: "scorm12" | "xapi"; title: string; launchPath: string; files: number; bytes: number; createdAt: string };

export type LearnerOption = { key: string; labels: Texts };
export type LearnerQuestion = { key: string; kind: QuestionKind; prompts: Texts; options: LearnerOption[] };
export type LearnerQuiz = { passMark: number; attempts: number; attemptsUsed: number; attemptsLeft: number; bestScore: number | null; questions: LearnerQuestion[] };
export type LearnerLesson = { key: string; kind: LessonKind; titles: Texts; sources: Record<string, string> | null; minutes: number | null; done: boolean; quiz: LearnerQuiz | null };

export type LearnerState = {
  state: LearnerStateKind;
  assignmentId: string | null;
  source: AssignmentSource | null;
  dueOn: string | null;
  lastLessonKey: string | null;
  percent: number;
  startedAt: string | null;
  completedAt: string | null;
  completedVersion: number | null;
  certificateId: string | null;
};

export type LearnerCourse = {
  id: string;
  version: number;
  category: string | null;
  titles: Texts;
  summaries: Texts;
  minutes: number | null;
  locales: string[];
  lessons: LearnerLesson[];
  me: LearnerState;
  canEnrol: boolean;
  manages: boolean;
  /** A course from the LMS: the address that opens it there, left out while the LMS is unavailable. */
  external?: { available: boolean; launchUrl: string | null } | null;
};

export type AttemptResult = { score: number; passed: boolean; course: LearnerCourse };

export type CatalogueCourse = {
  id: string;
  titles: Texts;
  summaries: Texts;
  category: string | null;
  minutes: number | null;
  lessons: number;
  openEnrolment: boolean;
  state: LearnerStateKind;
  percent: number;
  external?: boolean;
};

export type MyCourse = {
  courseId: string;
  assignmentId: string;
  titles: Texts;
  category: string | null;
  minutes: number | null;
  source: AssignmentSource;
  dueOn: string | null;
  state: LearnerStateKind;
  percent: number;
  startedAt: string | null;
  completedAt: string | null;
  certificateId: string | null;
  external?: boolean;
};

export type MyLearning = { assigned: MyCourse[]; inProgress: MyCourse[]; completed: MyCourse[] };

export type PathCourseView = { courseId: string; titles: Texts; status: string; requiresPrevious: boolean; state: LearnerStateKind; percent: number; locked: boolean };
export type PathView = {
  id: string;
  orgUnitId: string | null;
  status: "draft" | "published" | "retired";
  titles: Texts;
  summaries: Texts;
  audience: Audience;
  courses: PathCourseView[];
  rowVersion: number;
  manages: boolean;
};
export type PathSummary = { id: string; titles: Texts; summaries: Texts; status: string; courses: number; completed: number };

export type CertificateView = {
  id: string;
  code: string;
  courseId: string;
  courseTitles: Texts;
  version: number;
  learner: { id: string; displayName: string };
  issuedAt: string;
  expiresAt: string | null;
};

// ---- Building ----

/** Who sees a course in the catalogue: any clause matches; no clauses means everyone (spec 009 audience). */
export type Audience = { anyOf?: { orgUnits?: { id: string; subtree?: boolean }[] }[] | null };

export type QuizOption = { key: string; labels: Texts; correct: boolean };
export type QuizQuestion = { key: string; kind: QuestionKind; prompts: Texts; options: QuizOption[] };
export type QuizDefinition = { passMark: number; attempts: number; questions: QuizQuestion[] };
export type LessonDefinition = { key: string; kind: LessonKind; titles: Texts; sources?: Record<string, string> | null; quiz?: QuizDefinition | null; minutes?: number | null };
export type CourseDefinition = { titles: Texts; summaries: Texts; minutes?: number | null; lessons: LessonDefinition[] };

export type CourseDetail = {
  id: string;
  orgUnitId: string | null;
  unitNames: Texts | null;
  category: string | null;
  status: "draft" | "published" | "retired";
  audience: Audience;
  openEnrolment: boolean;
  draft: CourseDefinition;
  revision: number;
  draftChanged: boolean;
  publishedVersion: number | null;
  changedSources: { lessonKey: string; locale: string }[];
  updatedAt: string;
  externalId?: string | null;
  launchUrl?: string | null;
};

export type CourseSummary = {
  id: string;
  titles: Texts;
  category: string | null;
  status: "draft" | "published" | "retired";
  orgUnitId: string | null;
  publishedVersion: number | null;
  draftChanged: boolean;
  lessons: number;
  updatedAt: string;
  external?: boolean;
};

/** Where the LMS sync stands (spec 028 US6). */
export type LmsStatus = {
  configured: boolean;
  available: boolean;
  catalogueAt: string | null;
  completionsAt: string | null;
  courses: number;
  waiting: number;
  refused: number;
};

export type QuizStanding = { lessonKey: string; used: number; allowed: number; bestScore: number | null; passed: boolean };
export type CourseLearner = {
  assignmentId: string;
  learner: { id: string; displayName: string };
  source: AssignmentSource;
  status: "open" | "completed";
  dueOn: string | null;
  percent: number;
  completedAt: string | null;
  completedVersion: number | null;
  quizzes: QuizStanding[];
};

// ---- Helpers ----

export type AssignmentRule = {
  id: string;
  courseId: string;
  orgUnitId: string | null;
  audience: Audience;
  people: { id: string; displayName: string }[];
  dueDays: number;
  recurrenceMonths: number | null;
  graceDays: number;
  status: "active" | "ended";
  createdAt: string;
  covered: number;
};

export type ReportState = "assigned" | "overdue" | "completed" | "expired";
export type ReportCounts = { assigned: number; completed: number; overdue: number; expired: number };
export type ReportCourse = { id: string; titles: Texts; status: string; category: string | null };
export type UnitReport = { orgUnitId: string; names: Texts; parentId: string | null; counts: ReportCounts };
export type CourseReport = { courseId: string; titles: Texts; wholeTenant: boolean; total: ReportCounts; units: UnitReport[] };
export type ReportPerson = {
  person: { id: string; displayName: string };
  state: ReportState;
  source: AssignmentSource;
  dueOn: string | null;
  completedAt: string | null;
  version: number | null;
  score: number | null;
  certificateCode: string | null;
  certificateId: string | null;
};
export type TeamCourse = { courseId: string; titles: Texts; state: ReportState; source: AssignmentSource; dueOn: string | null; completedAt: string | null };
export type TeamMember = { person: { id: string; displayName: string }; courses: TeamCourse[] };

export const learningHref = "/learning";
export const catalogueHref = "/learning/catalogue";
export const manageHref = "/learning/manage";
export const reportsHref = "/learning/reports";
export const teamHref = "/learning/team";
export const reportHref = (id: string) => `/learning/reports/${encodeURIComponent(id)}`;
export const courseHref = (id: string) => `/learning/courses/${encodeURIComponent(id)}`;
export const pathHref = (id: string) => `/learning/paths/${encodeURIComponent(id)}`;
export const builderHref = (id: string) => `/learning/manage/courses/${encodeURIComponent(id)}`;
export const pathBuilderHref = (id: string) => `/learning/manage/paths/${encodeURIComponent(id)}`;
export const certificateHref = (id: string) => `/learning/certificates/${encodeURIComponent(id)}`;
export const certificatePdf = (id: string) => `${learningApi}/certificates/${encodeURIComponent(id)}/pdf`;

/** The text in the viewer's language, then English, then any language it has. */
export function localText(texts: Texts | null | undefined, locale: string, fallback = ""): string {
  return texts?.[locale] ?? texts?.en ?? Object.values(texts ?? {})[0] ?? fallback;
}

/** The language of a course the viewer reads it in: theirs when the course has it, else the course's first. */
export function courseLocale(locales: string[], locale: string): string {
  return locales.includes(locale) ? locale : locales.includes("en") ? "en" : (locales[0] ?? locale);
}

/** A guid as the API writes it; pages refuse anything else before calling the API. */
export const isGuid = (value: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);

export async function send(url: string, method: "POST" | "PUT", body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/** A call's result, or the API's messages (already in the viewer's language) when it was refused. */
export async function call<T>(request: () => Promise<Response>, failed: string): Promise<{ ok: true; value: T } | { ok: false; messages: string[] }> {
  try {
    const response = await request();
    if (response.ok) return { ok: true, value: (await response.json()) as T };
    return { ok: false, messages: await problemMessages(response) };
  } catch {
    return { ok: false, messages: [failed] };
  }
}
