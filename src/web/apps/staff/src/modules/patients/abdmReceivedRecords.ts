import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

export interface AbdmReceivedRecord {
  id: string;
  artefactId: string;
  hipName: string | null;
  careContextReference: string;
  title: string | null;
  recordDate: string | null;
  receivedAt: string;
  checksumMatches: boolean;
  error: string | null;
}

export interface AbdmReceivedRecordView {
  record: AbdmReceivedRecord;
  bundle: FhirBundle | null;
  eraseAt: string;
}

export interface FhirResource {
  resourceType?: string;
  [key: string]: unknown;
}

export interface FhirBundle {
  resourceType: 'Bundle';
  entry?: { resource?: FhirResource }[];
}

/** One line of a received bundle as a doctor reads it. */
export interface BundleItem {
  type: string;
  label: string;
  detail: string | null;
}

/** A file inside a received bundle (a PDF of the report, a scan). */
export interface BundleAttachment {
  title: string;
  contentType: string;
  data: string;
}

export const receivedRecordQueries = {
  list: (patientId: string) =>
    queryOptions({
      queryKey: ['abdm', 'received-records', patientId],
      queryFn: ({ signal }) =>
        api.get<AbdmReceivedRecord[]>(`/api/v1/abdm/patients/${patientId}/received-records`, { signal }),
    }),
};

/** The parts of a bundle that are about the patient's care, not who wrote it or for whom. */
const SKIPPED = new Set(['Composition', 'Patient', 'Practitioner', 'PractitionerRole', 'Organization', 'Encounter']);

const TYPE_NAMES: Record<string, string> = {
  Condition: 'Diagnosis',
  Observation: 'Observation',
  MedicationRequest: 'Medicine',
  MedicationStatement: 'Medicine',
  AllergyIntolerance: 'Allergy',
  Procedure: 'Procedure',
  DiagnosticReport: 'Report',
  DocumentReference: 'Document',
  Immunization: 'Immunisation',
  ServiceRequest: 'Advice',
  CarePlan: 'Care plan',
  Appointment: 'Follow-up',
};

const str = (value: unknown): string | null => (typeof value === 'string' && value.trim() ? value.trim() : null);

function conceptText(value: unknown): string | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const concept = value as { text?: unknown; coding?: { display?: unknown; code?: unknown }[] };
  return (
    str(concept.text) ??
    concept.coding?.map((c) => str(c.display) ?? str(c.code)).find((d): d is string => d !== null) ??
    null
  );
}

function quantity(value: unknown): string | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const q = value as { value?: unknown; unit?: unknown };
  return typeof q.value === 'number' ? [q.value, str(q.unit)].filter((x) => x !== null).join(' ') : null;
}

/**
 * The readable content of a FHIR bundle another provider sent: one line per clinical resource (its kind, what it is,
 * and a value or status), and the files inside it. Text is read, never rendered as HTML.
 */
export function bundleSummary(bundle: FhirBundle | null): { items: BundleItem[]; attachments: BundleAttachment[] } {
  const items: BundleItem[] = [];
  const attachments: BundleAttachment[] = [];
  for (const entry of bundle?.entry ?? []) {
    const r = entry.resource;
    const type = r?.resourceType;
    if (!r || !type || SKIPPED.has(type)) {
      continue;
    }

    if (type === 'DocumentReference' || type === 'Binary') {
      const contents = type === 'Binary' ? [{ attachment: r }] : ((r.content as { attachment?: unknown }[]) ?? []);
      for (const c of contents) {
        const a = c.attachment as { contentType?: unknown; data?: unknown; title?: unknown } | undefined;
        if (a && str(a.contentType) && str(a.data)) {
          attachments.push({
            title: str(a.title) ?? conceptText(r.type) ?? 'Document',
            contentType: str(a.contentType)!,
            data: str(a.data)!,
          });
        }
      }
    }

    const label =
      conceptText(r.code) ??
      conceptText(r.medicationCodeableConcept) ??
      conceptText(r.vaccineCode) ??
      conceptText(r.type) ??
      str(r.description) ??
      str(r.title) ??
      TYPE_NAMES[type] ??
      type;
    const detail =
      quantity(r.valueQuantity) ??
      str(r.valueString) ??
      conceptText(r.valueCodeableConcept) ??
      str(r.conclusion) ??
      (Array.isArray(r.dosageInstruction)
        ? str((r.dosageInstruction[0] as { text?: unknown } | undefined)?.text)
        : null) ??
      conceptText(r.clinicalStatus) ??
      null;
    items.push({ type: TYPE_NAMES[type] ?? type, label, detail });
  }

  return { items, attachments };
}
