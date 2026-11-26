import type { PersonCard } from "./types";

/** Skills and Find an expert (spec 010 R2 US6). The API decides who sees what; these are its shapes. */
export type Proficiency = "basic" | "good" | "expert";
export const proficiencies: readonly Proficiency[] = ["basic", "good", "expert"];

export type SkillView = { id: string; names: Record<string, string>; status: "active" | "suggested"; people: number };

export type SkillListView = {
  available: boolean;
  enabled: boolean;
  allowFreeText: boolean;
  turnedOffAt: string | null;
  skills: SkillView[];
  canManage: boolean;
  version: number;
  eTag: string;
};

export type PersonSkillView = {
  skillId: string;
  names: Record<string, string>;
  proficiency: Proficiency;
  suggested: boolean;
  endorsements: number;
  endorsedByMe: boolean;
  canEndorse: boolean;
};

export type PersonSkillsView = { enabled: boolean; items: PersonSkillView[]; isSelf: boolean };

export type Endorser = { id: string; displayName: string; hidden: boolean };

export type OwnSkillView = { skillId: string; names: Record<string, string>; proficiency: Proficiency; suggested: boolean; endorsers: Endorser[] };

export type OwnSkillsView = { enabled: boolean; allowFreeText: boolean; items: OwnSkillView[]; choices: SkillView[] };

export type ExpertView = { person: PersonCard; skillId: string; skillNames: Record<string, string>; proficiency: Proficiency; endorsements: number };

export type ExpertsView = { enabled: boolean; matches: SkillView[]; items: ExpertView[] };

export const expertsHref = (q?: string) => (q ? `/people/experts?q=${encodeURIComponent(q)}` : "/people/experts");
