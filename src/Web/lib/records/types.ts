/** Shapes of the spec 011 R2 records management API (retention labels, disposition, legal holds), as the browser reads them. */
import type { Names } from "@/lib/documents/types";

export type RetentionTrigger = "created" | "modified" | "effective" | "superseded" | "field";
export type RetentionAction = "delete" | "review" | "archive";

export type LabelView = {
  id: string;
  names: Names;
  periodMonths: number;
  trigger: RetentionTrigger;
  triggerField: string | null;
  action: RetentionAction;
  status: "active" | "retired";
  documents: number;
  version: number;
  declaresRecord: boolean;
};

export type RecordsTypeView = { id: string; names: Names; labelId: string | null; dateFields: string[] };

export type RecordsLibraryView = { id: string; names: Names; scope: string; labelId: string | null; types: RecordsTypeView[] };

export type DispositionItem = {
  documentId: string;
  libraryId: string;
  name: string;
  number: string | null;
  labelId: string;
  labelNames: Names;
  endedAt: string | null;
  dueAt: string;
  held: boolean;
};

export type DispositionRecordView = {
  id: string;
  documentId: string;
  number: string | null;
  labelNames: Names;
  versions: number;
  mode: "reviewed" | "automatic";
  approvedBy: string | null;
  disposedAt: string;
};

export type HoldView = {
  id: string;
  name: string;
  reason: string;
  custodianId: string | null;
  from: string | null;
  to: string | null;
  libraryId: string | null;
  folderId: string | null;
  documentId: string | null;
  includeAudit: boolean;
  status: "active" | "released";
  createdAt: string;
  createdBy: string | null;
  releasedAt: string | null;
  documents: number;
};

export type HeldDocument = { id: string; libraryId: string; name: string; number: string | null };

export type HoldDetail = { hold: HoldView; documents: HeldDocument[] };

/** A document declared a record (spec 011 R3): by a records manager or by a label that declares records. */
export type RecordItem = {
  documentId: string;
  libraryId: string;
  name: string;
  number: string | null;
  recordAt: string;
  source: "manual" | "label";
  labelId: string | null;
};

/** A document's retention and record state, for records managers. */
export type DocumentRetentionView = {
  documentId: string;
  labelId: string | null;
  source: string | null;
  endsAt: string | null;
  disposition: string | null;
  held: boolean;
  recordAt: string | null;
  recordSource: "manual" | "label" | null;
};

export type DispositionCertificateView = {
  id: string;
  code: string;
  from: string;
  to: string;
  records: number;
  digest: string;
  issuedBy: string;
  issuerName: string;
  issuedAt: string;
};

export type DispositionCertificateDetail = { certificate: DispositionCertificateView; items: DispositionRecordView[]; intact: boolean };

export const recordsHref = "/records";

export const triggers: readonly RetentionTrigger[] = ["created", "modified", "effective", "superseded", "field"];
export const actions: readonly RetentionAction[] = ["review", "delete", "archive"];
