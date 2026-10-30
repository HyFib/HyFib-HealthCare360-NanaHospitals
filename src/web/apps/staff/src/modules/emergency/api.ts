import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * The emergency department: /api/v1/emergency. The ER board, ESI triage (the server computes the level), the doctor's
 * first look and the disposition, and the medico-legal case register with its police intimation.
 */

export type ArrivalMode = 'WalkIn' | 'GovernmentAmbulance' | 'PrivateAmbulance' | 'Police' | 'ReferredIn';
export type EmergencyDisposition = 'Discharged' | 'Admitted' | 'ReferredOut' | 'Lama' | 'Absconded' | 'Died';
export type MlcCause =
  | 'RoadTrafficAccident'
  | 'Assault'
  | 'SexualAssault'
  | 'Burns'
  | 'Poisoning'
  | 'Fall'
  | 'Hanging'
  | 'Drowning'
  | 'AnimalBite'
  | 'Firearm'
  | 'IndustrialAccident'
  | 'Unknown'
  | 'Other'
  | 'AcidAttack';
export type MlcBroughtBy = 'Self' | 'Relative' | 'Police' | 'Public' | 'Ambulance';
export type MlcIntimationMode = 'Phone' | 'Written' | 'InPerson';
/** Who was told of a sexual offence against a child (POCSO Act 2012, s.19(1)). */
export type PocsoReportedTo = 'SpecialJuvenilePoliceUnit' | 'LocalPolice';
/** Who was present at a child's medical examination (POCSO Act 2012, s.27(3) and (4)). */
export type ExaminationAttendant = 'Parent' | 'TrustedPerson' | 'WomanNominatedByHead';
/** A safeguard of the POCSO Act 2012, s.27, for a child's examination (server `PocsoSafeguard`). */
export type PocsoSafeguard = 'WomanDoctor' | 'PersonPresent';

export interface ErBoardRow {
  caseId: string;
  visitId: string;
  patientId: string;
  patientName: string;
  uhid: string;
  gender: string | null;
  ageYears: number | null;
  arrivedAt: string;
  arrivalMode: ArrivalMode;
  level: number | null;
  chiefComplaint: string | null;
  minutesInDepartment: number;
  doorToTriageMinutes: number | null;
  doorToDoctorMinutes: number | null;
  doctorName: string | null;
  isMlc: boolean;
  mlcNumber: string | null;
  mlcIntimationOverdue: boolean;
  version: number;
}

export interface ErBoard {
  rows: ErBoardRow[];
  untriaged: number;
  /** Patients at levels 1 to 5. */
  byLevel: number[];
}

export interface EsiTriage {
  id: string;
  assessedAt: string;
  assessedByName: string;
  chiefComplaint: string;
  computedLevel: number;
  level: number;
  reasons: string[];
  overrideReason: string | null;
  pulse: number | null;
  respiratoryRate: number | null;
  spO2: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  temperatureCelsius: number | null;
  gcs: number | null;
  painScore: number | null;
}

export interface Mlc {
  id: string;
  number: string;
  identifiedAt: string;
  broughtBy: MlcBroughtBy;
  broughtByName: string | null;
  cause: MlcCause;
  incidentAt: string | null;
  incidentPlace: string | null;
  injuries: string;
  consciousOnArrival: boolean | null;
  smellOfAlcohol: boolean | null;
  doctorName: string;
  doctorRegistrationNumber: string | null;
  policeStation: string;
  intimatedAt: string | null;
  intimationMode: MlcIntimationMode | null;
  receivedBy: string | null;
  policeEntryNumber: string | null;
  intimationOverdue: boolean;
  version: number;
  /** Under 18 on the day of the incident. */
  isChild: boolean;
  /** A sexual offence against a child: the POCSO Act 2012 applies. */
  isPocso: boolean;
  reportedTo: PocsoReportedTo | null;
  examination: MlcExamination | null;
  investigatingOfficer: string | null;
  reportForwardedAt: string | null;
  /** When the examination's report is due at the investigating officer: seven days after it (BNSS s.184(6)). */
  reportDueAt: string | null;
  reportOverdue: boolean;
}

/** A sexual-assault victim's medical examination (BNSS 2023, s.184; for a child, POCSO Act 2012, s.27). */
export interface MlcExamination {
  examinerId: string;
  examinerName: string;
  examinerRegistration: string | null;
  startedAt: string;
  endedAt: string;
  consentGivenBy: string;
  attendantKind: ExaminationAttendant | null;
  attendantName: string | null;
  /** For a case found to be a child's only after the examination was recorded: what of s.27 the examination lacks. */
  missingSafeguards: PocsoSafeguard[];
}

export interface EmergencyCase {
  id: string;
  visitId: string;
  patientId: string;
  patientName: string;
  uhid: string;
  gender: string | null;
  ageYears: number | null;
  arrivedAt: string;
  arrivalMode: ArrivalMode;
  level: number | null;
  triagedAt: string | null;
  doctorSeenAt: string | null;
  doctorName: string | null;
  disposition: EmergencyDisposition | null;
  disposedAt: string | null;
  disposedByName: string | null;
  dispositionNotes: string | null;
  referredTo: string | null;
  triages: EsiTriage[];
  mlc: Mlc | null;
  version: number;
}

export interface TriageInput {
  chiefComplaint: string;
  arrivalMode: ArrivalMode;
  needsLifeSavingIntervention: boolean;
  highRiskSituation: boolean;
  confusedLethargicOrDisoriented: boolean;
  severeDistress: boolean;
  expectedResources: number;
  pulse: number | null;
  respiratoryRate: number | null;
  spO2: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  temperatureCelsius: number | null;
  gcs: number | null;
  painScore: number | null;
  level: number | null;
  overrideReason: string | null;
  version: number;
}

export interface TriagePreview {
  level: number;
  reasons: string[];
  dangerZoneVitals: boolean;
}

export interface MlcInput {
  broughtBy: MlcBroughtBy;
  broughtByName: string | null;
  cause: MlcCause;
  incidentAt: string | null;
  incidentPlace: string | null;
  injuries: string;
  consciousOnArrival: boolean | null;
  smellOfAlcohol: boolean | null;
  policeStation: string;
  doctorRegistrationNumber: string | null;
  version: number | null;
  /** Whether the patient is under 18, said for a sexual assault when the date of birth is not known. */
  childVictim: boolean | null;
}

export interface ExaminationInput {
  examinerId: string;
  startedAt: string;
  endedAt: string;
  consentGivenBy: string;
  attendantKind: ExaminationAttendant | null;
  attendantName: string | null;
  version: number;
}

export interface MlcRegisterRow {
  id: string;
  caseId: string;
  number: string;
  identifiedAt: string;
  patientName: string;
  uhid: string;
  cause: MlcCause;
  policeStation: string;
  intimatedAt: string | null;
  receivedBy: string | null;
  intimationOverdue: boolean;
  doctorName: string;
  isPocso: boolean;
  reportDueAt: string | null;
  reportForwardedAt: string | null;
  reportOverdue: boolean;
  /** A child's examination recorded before the case was known to be a child's lacks a safeguard of POCSO s.27. */
  pocsoSafeguardsMissing: boolean;
}

export const ER_PERMISSIONS = {
  board: 'clinical.er.board',
  triage: 'clinical.er.triage',
  disposition: 'clinical.er.disposition',
  mlc: 'clinical.er.mlc',
  register: 'mrd.mlc.register',
} as const;

export const ER_READERS = [
  ER_PERMISSIONS.board,
  ER_PERMISSIONS.triage,
  ER_PERMISSIONS.disposition,
  ER_PERMISSIONS.mlc,
] as const;
export const MLC_READERS = [ER_PERMISSIONS.mlc, ER_PERMISSIONS.register] as const;

/** The ESI levels as the department names them. */
export const ESI_LEVELS: Record<
  number,
  { name: string; tone: 'danger' | 'severe' | 'warning' | 'success' | 'informative' }
> = {
  1: { name: 'Resuscitation', tone: 'danger' },
  2: { name: 'Emergent', tone: 'severe' },
  3: { name: 'Urgent', tone: 'warning' },
  4: { name: 'Less urgent', tone: 'success' },
  5: { name: 'Non-urgent', tone: 'informative' },
};

export const ARRIVAL_LABELS: Record<ArrivalMode, string> = {
  WalkIn: 'Walked in',
  GovernmentAmbulance: '108 ambulance',
  PrivateAmbulance: 'Private ambulance',
  Police: 'Brought by police',
  ReferredIn: 'Referred in',
};

export const DISPOSITION_LABELS: Record<EmergencyDisposition, string> = {
  Discharged: 'Discharged',
  Admitted: 'Admitted',
  ReferredOut: 'Referred to another hospital',
  Lama: 'Left against medical advice',
  Absconded: 'Absconded',
  Died: 'Died',
};

export const CAUSE_LABELS: Record<MlcCause, string> = {
  RoadTrafficAccident: 'Road traffic accident',
  Assault: 'Assault',
  SexualAssault: 'Sexual assault',
  Burns: 'Burns',
  AcidAttack: 'Acid attack',
  Poisoning: 'Poisoning',
  Fall: 'Fall',
  Hanging: 'Hanging',
  Drowning: 'Drowning',
  AnimalBite: 'Animal or snake bite',
  Firearm: 'Firearm injury',
  IndustrialAccident: 'Industrial accident',
  Unknown: 'Not known',
  Other: 'Other',
};

/**
 * Whether the victim of a case of this cause is treated free of cost (BNSS 2023, s.397): a sexual assault or an acid
 * attack. The server marks the visit from the MLC entry (Clinical's `FreeTreatment.Covers`), and its bills come to nothing.
 */
export const isTreatedFree = (cause: MlcCause) => cause === 'SexualAssault' || cause === 'AcidAttack';

export const REPORTED_TO_LABELS: Record<PocsoReportedTo, string> = {
  SpecialJuvenilePoliceUnit: 'Special Juvenile Police Unit',
  LocalPolice: 'Local police',
};

export const SAFEGUARD_LABELS: Record<PocsoSafeguard, string> = {
  WomanDoctor: 'a girl child is examined by a woman doctor (s.27(2))',
  PersonPresent:
    'a parent, a person the child trusts or a woman the head of the hospital nominated is present (s.27(3)–(4))',
};

export const ATTENDANT_LABELS: Record<ExaminationAttendant, string> = {
  Parent: 'Parent',
  TrustedPerson: 'A person the child trusts',
  WomanNominatedByHead: 'A woman nominated by the head of the hospital',
};

export const BROUGHT_BY_LABELS: Record<MlcBroughtBy, string> = {
  Self: 'Came alone',
  Relative: 'Relative or friend',
  Police: 'Police',
  Public: 'Member of the public (Good Samaritan)',
  Ambulance: 'Ambulance',
};

/** "35 min", "2 h 05 min": how long the patient has waited or stayed. */
export function formatMinutes(minutes: number): string {
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  return `${hours} h ${String(minutes % 60).padStart(2, '0')} min`;
}

/** ESI's target for a doctor to see the patient, in minutes: immediately for level 1, within ten minutes for level 2. */
export function doctorWaitBreached(level: number | null, minutesWaiting: number, seen: boolean): boolean {
  if (seen || level === null) {
    return false;
  }
  return level === 1 || (level === 2 && minutesWaiting > 10);
}

export const erKeys = {
  all: ['emergency'] as const,
  board: () => ['emergency', 'board'] as const,
  case: (id: string) => ['emergency', 'case', id] as const,
  register: (from?: string, to?: string) => ['emergency', 'mlc', from ?? '', to ?? ''] as const,
};

const base = '/api/v1/emergency';

export const erQueries = {
  board: () =>
    queryOptions({ queryKey: erKeys.board(), queryFn: ({ signal }) => api.get<ErBoard>(`${base}/board`, { signal }) }),
  case: (id: string) =>
    queryOptions({
      queryKey: erKeys.case(id),
      queryFn: ({ signal }) => api.get<EmergencyCase>(`${base}/cases/${id}`, { signal }),
    }),
  register: (from?: string, to?: string) =>
    queryOptions({
      queryKey: erKeys.register(from, to),
      queryFn: ({ signal }) => api.get<MlcRegisterRow[]>(`${base}/mlc`, { query: { from, to }, signal }),
    }),
};

export const erApi = {
  /** The level for the answers and vitals typed so far (the complaint, level and reason are not needed). */
  preview: (id: string, input: TriageInput) => {
    const {
      chiefComplaint: _complaint,
      arrivalMode: _arrival,
      level: _level,
      overrideReason: _reason,
      version: _version,
      ...answers
    } = input;
    return api.post<TriagePreview>(`${base}/cases/${id}/triage/preview`, answers);
  },
  triage: (id: string, input: TriageInput) => api.post<EmergencyCase>(`${base}/cases/${id}/triage`, input),
  seen: (id: string, version: number) => api.post<EmergencyCase>(`${base}/cases/${id}/seen`, { version }),
  dispose: (
    id: string,
    disposition: EmergencyDisposition,
    notes: string | null,
    referredTo: string | null,
    version: number,
  ) => api.post<EmergencyCase>(`${base}/cases/${id}/disposition`, { disposition, notes, referredTo, version }),
  saveMlc: (id: string, input: MlcInput) => api.put<EmergencyCase>(`${base}/cases/${id}/mlc`, input),
  intimate: (
    id: string,
    intimatedAt: string,
    mode: MlcIntimationMode,
    receivedBy: string,
    policeEntryNumber: string | null,
    reportedTo: PocsoReportedTo | null,
    version: number,
  ) =>
    api.post<EmergencyCase>(`${base}/cases/${id}/mlc/intimation`, {
      intimatedAt,
      mode,
      receivedBy,
      policeEntryNumber,
      reportedTo,
      version,
    }),
  recordExamination: (id: string, input: ExaminationInput) =>
    api.post<EmergencyCase>(`${base}/cases/${id}/mlc/examination`, input),
  forwardReport: (id: string, forwardedAt: string, investigatingOfficer: string, version: number) =>
    api.post<EmergencyCase>(`${base}/cases/${id}/mlc/report`, { forwardedAt, investigatingOfficer, version }),
  mlcDocument: (id: string) => `${base}/cases/${id}/mlc/document`,
};
