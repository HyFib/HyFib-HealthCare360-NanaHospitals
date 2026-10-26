import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { PHARMACY_PERMISSIONS } from './permissions';

// Suspected adverse drug reactions (server `AdrEndpoints`): reported by the clinician who saw the reaction on the
// Pharmacovigilance Programme of India (PvPI) form, assessed (WHO-UMC causality) and sent to the ADR monitoring centre by
// pharmacovigilance. The form names the patient by initials only.

export const SERIOUSNESS = [
  ['Death', 1, 'Death'],
  ['LifeThreatening', 2, 'Life-threatening'],
  ['Hospitalisation', 4, 'Hospitalisation, initial or prolonged'],
  ['Disability', 8, 'Disability'],
  ['CongenitalAnomaly', 16, 'Congenital anomaly'],
  ['RequiredIntervention', 32, 'Required intervention to prevent permanent damage'],
  ['OtherMedicallyImportant', 64, 'Other medically important'],
] as const;

export const OUTCOMES = [
  'Recovered',
  'Recovering',
  'NotRecovered',
  'RecoveredWithSequelae',
  'Fatal',
  'Unknown',
] as const;
export type AdrOutcome = (typeof OUTCOMES)[number];
export const outcomeLabel: Record<AdrOutcome, string> = {
  Recovered: 'Recovered',
  Recovering: 'Recovering',
  NotRecovered: 'Not recovered',
  RecoveredWithSequelae: 'Recovered with sequelae',
  Fatal: 'Fatal',
  Unknown: 'Unknown',
};

export const CAUSALITIES = [
  'Certain',
  'Probable',
  'Possible',
  'Unlikely',
  'ConditionalUnclassified',
  'UnassessableUnclassifiable',
] as const;
export type WhoUmcCausality = (typeof CAUSALITIES)[number];
export const causalityLabel: Record<WhoUmcCausality, string> = {
  Certain: 'Certain',
  Probable: 'Probable / likely',
  Possible: 'Possible',
  Unlikely: 'Unlikely',
  ConditionalUnclassified: 'Conditional / unclassified',
  UnassessableUnclassifiable: 'Unassessable / unclassifiable',
};

export type ChallengeResult = 'NotApplicable' | 'Yes' | 'No' | 'Unknown';
export type AdrActionTaken = 'Unknown' | 'Withdrawn' | 'DoseReduced' | 'DoseIncreased' | 'NotChanged';
export type AdrReportStatus = 'Draft' | 'Submitted';

/**
 * The server sends `AdrSeriousness` as a comma-separated list of names ("Hospitalisation, RequiredIntervention"),
 * "None" when not serious; it takes the same back.
 */
export type AdrSeriousness = string;

export interface AdrDrug {
  name: string;
  manufacturer: string | null;
  batchNumber: string | null;
  expiryDate: string | null;
  dose: string | null;
  route: string | null;
  frequency: string | null;
  startedOn: string | null;
  stoppedOn: string | null;
  indication: string | null;
  suspected: boolean;
  action: AdrActionTaken;
  dechallenge: ChallengeResult;
  rechallenge: ChallengeResult;
}

export interface AdrReport {
  id: string;
  number: string;
  patientId: string;
  patientInitials: string;
  uhid: string;
  sex: string | null;
  ageYears: number | null;
  incidentNumber: string | null;
  status: AdrReportStatus;
  learnedOn: string;
  dueBy: string | null;
  overdue: boolean;
  reaction: string;
  reactionStartedOn: string;
  reactionStoppedOn: string | null;
  seriousness: AdrSeriousness;
  outcome: AdrOutcome;
  weightKg: number | null;
  relevantTests: string | null;
  medicalHistory: string | null;
  drugs: AdrDrug[];
  reporterName: string;
  reporterDesignation: string | null;
  causality: WhoUmcCausality | null;
  assessedByName: string | null;
  submittedOn: string | null;
  submissionReference: string | null;
  version: number;
}

export interface AdrReportListItem {
  id: string;
  number: string;
  patientInitials: string;
  uhid: string;
  reaction: string;
  suspectedDrugs: string;
  serious: boolean;
  status: AdrReportStatus;
  learnedOn: string;
  dueBy: string | null;
  overdue: boolean;
  reporterName: string;
  causality: WhoUmcCausality | null;
}

export interface DispensedMedicine {
  name: string;
  genericName: string | null;
  manufacturer: string | null;
  batchNumber: string;
  expiryDate: string | null;
  givenAt: string;
}

export interface SaveAdrRequest {
  patientId: string;
  incidentNumber: string | null;
  learnedOn: string;
  reaction: string;
  reactionStartedOn: string;
  reactionStoppedOn: string | null;
  seriousness: AdrSeriousness;
  outcome: AdrOutcome;
  weightKg: number | null;
  relevantTests: string | null;
  medicalHistory: string | null;
  drugs: AdrDrug[];
  reporterDesignation: string | null;
  version?: number;
}

export const adrKeys = {
  all: ['pharmacy', 'adr'] as const,
  list: () => ['pharmacy', 'adr', 'list'] as const,
  report: (id: string) => ['pharmacy', 'adr', 'report', id] as const,
  medicines: (patientId: string) => ['pharmacy', 'adr', 'medicines', patientId] as const,
};

const base = '/api/v1/pharmacy/adr';

export const adrQueries = {
  list: () =>
    queryOptions({ queryKey: adrKeys.list(), queryFn: ({ signal }) => api.get<AdrReportListItem[]>(base, { signal }) }),
  report: (id: string) =>
    queryOptions({
      queryKey: adrKeys.report(id),
      queryFn: ({ signal }) => api.get<AdrReport>(`${base}/${id}`, { signal }),
    }),
  medicines: (patientId: string) =>
    queryOptions({
      queryKey: adrKeys.medicines(patientId),
      queryFn: ({ signal }) => api.get<DispensedMedicine[]>(`${base}/patients/${patientId}/medicines`, { signal }),
    }),
};

export const adrApi = {
  report: (request: SaveAdrRequest) => api.post<AdrReport>(base, request),
  correct: (id: string, request: SaveAdrRequest) => api.put<AdrReport>(`${base}/${id}`, request),
  submit: (id: string, causality: WhoUmcCausality, submittedOn: string, reference: string | null, version: number) =>
    api.post<AdrReport>(`${base}/${id}/submit`, { causality, submittedOn, reference, version }),
};

/** The printable PvPI form of a report (`<path>/document`). */
export const adrDocument = (id: string) => `${base}/${id}`;

export const ADR_READERS = [PHARMACY_PERMISSIONS.adrReport, PHARMACY_PERMISSIONS.adrManage] as const;

/** The seriousness names chosen, as the server takes them ("None" when none). */
export const seriousnessValue = (chosen: readonly string[]) => (chosen.length === 0 ? 'None' : chosen.join(', '));

/** The names in a seriousness the server sent. */
export const seriousnessNames = (value: AdrSeriousness) =>
  value === 'None'
    ? []
    : value
        .split(',')
        .map((v) => v.trim())
        .filter(Boolean);

export const blankDrug = (suspected: boolean): AdrDrug => ({
  name: '',
  manufacturer: null,
  batchNumber: null,
  expiryDate: null,
  dose: null,
  route: null,
  frequency: null,
  startedOn: null,
  stoppedOn: null,
  indication: null,
  suspected,
  action: 'Unknown',
  dechallenge: 'NotApplicable',
  rechallenge: 'NotApplicable',
});

/** What stops a report from being saved, field by field (null when nothing does); dates as yyyy-mm-dd. */
export function adrProblems(input: {
  patientId: string | null;
  reaction: string;
  startedOn: string;
  stoppedOn: string;
  learnedOn: string;
  outcome: AdrOutcome;
  seriousness: readonly string[];
  drugs: AdrDrug[];
  today: string;
}) {
  const named = input.drugs.filter((d) => d.name.trim());
  return {
    patient: input.patientId ? null : 'Choose the patient.',
    reaction: input.reaction.trim() ? null : 'Describe the reaction.',
    startedOn: !input.startedOn
      ? 'When did it start?'
      : input.startedOn > input.today
        ? 'It cannot start in the future.'
        : null,
    stoppedOn:
      input.stoppedOn && (input.stoppedOn < input.startedOn || input.stoppedOn > input.today)
        ? 'It stops on or after it starts, and not in the future.'
        : null,
    learnedOn:
      !input.learnedOn || input.learnedOn > input.today || (input.startedOn && input.learnedOn < input.startedOn)
        ? 'The hospital learned of it on or after it started, and not in the future.'
        : null,
    outcome:
      (input.outcome === 'Fatal') !== input.seriousness.includes('Death')
        ? 'A fatal outcome and death as the seriousness go together.'
        : null,
    drugs: named.some((d) => d.suspected) ? null : 'Name at least one suspected drug.',
  };
}
