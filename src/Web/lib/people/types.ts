/** Spec 010 people directory: shapes returned by `/api/v1/people` and `/api/v1/me/team|profile`. */
export type Names = Record<string, string>;

export type UnitRef = { id: string; names: Names };
export type LocationRef = { key: string; label: string; names: Names };
export type FieldView = { key: string; value: string; names: Names | null };

export type PersonCard = {
  id: string;
  displayName: string;
  names: Names;
  jobTitle: string | null;
  unit: UnitRef | null;
  location: LocationRef | null;
  fields?: FieldView[] | null;
  level?: number | null;
};

export type PersonView = {
  id: string;
  displayName: string;
  names: Names;
  unit: UnitRef | null;
  units: UnitRef[];
  location: LocationRef | null;
  fields: FieldView[];
  manager: PersonCard | null;
  directReports: number;
  isSelf: boolean;
  canSetManager: boolean;
  canEditAdminFields: boolean;
  /** Approved leave covering today (spec 022 FR-005): dates only. */
  away?: { from: string; to: string } | null;
};

export type SearchPage = { items: PersonCard[]; next: number | null };
export type OrgChart = { person: PersonCard; chain: PersonCard[]; peers: PersonCard[]; peerCount: number; reports: PersonCard[]; reportCount: number };
export type TeamPage = { items: PersonCard[]; total: number; next: number | null };

export type OwnField = { key: string; value: string | null; source: string; visibility: string; editable: boolean; names: Names | null };
export type OwnProfile = { profile: PersonView; fields: OwnField[]; newcomerOptOut: boolean; allowNewcomerOptOut: boolean };

export type PolicyField = { key: string; source: string; visibility: string; sources: string[]; custom: boolean; names: Names };
export type Policy = {
  fields: PolicyField[];
  newcomerDays: number;
  allowNewcomerOptOut: boolean;
  locationAttribute: string;
  locationAttributes: string[];
  customFieldsAvailable: boolean;
  directorySyncedAt: string | null;
  version: number;
  eTag: string;
};

export type Location = { key: string; label: string; names: Names; people: number };

export const visibilities = ["everyone", "subtree", "managers_hr", "hr", "private"] as const;

/** Built-in field keys; custom fields start with `custom.` and carry their own names. */
export const builtInFields = ["email", "jobTitle", "department", "office", "workPhone", "mobilePhone", "preferredLanguage", "timeZone", "hireDate", "employeeNumber", "grade", "costCentre", "employmentType"] as const;

export function localName(names: Names | null | undefined, locale: string, fallback = ""): string {
  if (!names) return fallback;
  return names[locale] ?? fallback ?? names.en ?? Object.values(names)[0] ?? "";
}

/** The person's name in the viewer's language where one is set, else the directory name. */
export function personName(p: { displayName: string; names: Names }, locale: string): string {
  return p.names[locale] ?? p.displayName;
}

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/u).filter(Boolean);
  return parts
    .slice(0, 2)
    .map((p) => Array.from(p)[0] ?? "")
    .join("")
    .toUpperCase();
}

export function locationName(l: LocationRef | Location, locale: string): string {
  return l.names[locale] ?? l.label;
}

export const isPhoneField = (key: string) => key === "workPhone" || key === "mobilePhone";
