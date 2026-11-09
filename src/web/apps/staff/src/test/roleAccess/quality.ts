import type {
  CapaDetail,
  CapaListItem,
  IncidentDetail,
  IncidentListItem,
  IndicatorDto,
  IndicatorTrendDto,
  IndicatorValueDto,
  InvestigationDto,
  MyIncidentItem,
  QualityDashboard,
  RiskDetail,
  RiskListItem,
  RiskMatrixCell,
  ScorecardRow,
} from '@/modules/quality/api';
import type { DowntimeEvent, DowntimeSummary } from '@/modules/quality/downtimeApi';
import type { AuditListItem, AuditTemplate } from '@/modules/quality/auditApi';
import type { IndicatorDrilldown } from '@/modules/quality/computedApi';
import type { DeviceEpisode, HaiCaseListItem, HaiRateRow } from '@/modules/quality/haiApi';
import { blankDevice, type MvpiReport, type MvpiReportListItem } from '@/modules/quality/mvpi';
import { riskRating } from '@/modules/quality/rules';
import { paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Quality: an incident under review with an open action (so it can move to investigation or to actions), incidents in
// each state, actions open, in progress and done, a risk register with a heat map, and NABH indicators with values.

const now = Date.now();
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();
const dateAgo = (days: number) => daysAgo(days).slice(0, 10);
const dateIn = (days: number) => dateAgo(-days);
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

const ME = STAFF_USER.id;
const NURSE_MANAGER = { id: id('a3'), name: 'Sr. Mary Thomas' };
const ICU = { id: id('de04'), name: 'Intensive care unit' };
const PHARMACY = { id: id('de05'), name: 'Pharmacy' };

// ---------------------------------------------------------------------------------------------------- incidents

const INCIDENT_CAPA = id('ca02');

const INCIDENT: IncidentDetail = {
  id: RECORD_ID,
  incidentNumber: 'INC-26-00118',
  type: 'MedicationError',
  severity: 'Mild',
  isSentinel: false,
  sentinelKind: null,
  sentinelReason: null,
  status: 'UnderReview',
  nextStatuses: ['Investigation', 'CapaInProgress', 'Closed'],
  occurredAt: daysAgo(2.4),
  reportedAt: daysAgo(2.3),
  location: 'ICU bed 4',
  departmentId: ICU.id,
  departmentName: ICU.name,
  patientId: id('1'),
  involvedStaff: [{ id: id('a4'), name: 'Staff Nurse Kavita Pawar' }],
  description: 'Insulin given at 8 units instead of the prescribed 6 units; noticed at the next glucose check.',
  immediateAction: 'Doctor informed, glucose monitored hourly for 6 hours; no hypoglycaemia.',
  witnesses: 'Sr. Mary Thomas',
  isAnonymous: false,
  reporterName: 'Staff Nurse Kavita Pawar',
  assignedTo: NURSE_MANAGER,
  closedAt: null,
  closureNote: null,
  lastStatusNote: 'Assigned for review.',
  reopenCount: 0,
  investigationStatus: 'InProgress',
  capas: [
    {
      id: INCIDENT_CAPA,
      capaNumber: 'CAPA-26-0042',
      type: 'Corrective',
      action: 'Double-check of insulin doses by a second nurse in ICU',
      status: 'Open',
      dueDate: dateIn(14),
      isOverdue: false,
    },
  ],
  involvesDevice: false,
  isDeviceRelated: false,
  mvpiReports: [],
  version: 4,
};

const INVESTIGATION: InvestigationDto = {
  id: id('17e1'),
  incidentId: RECORD_ID,
  method: 'FiveWhys',
  problemStatement: 'A higher insulin dose than prescribed was given.',
  whys: ['The sliding scale was read from the old chart.', 'The new chart was not at the bedside.'],
  factors: [{ id: id('17f1'), category: 'Process', description: 'Old insulin chart not withdrawn', isRootCause: true }],
  findings: null,
  recommendations: null,
  investigators: [NURSE_MANAGER],
  status: 'InProgress',
  startedAt: daysAgo(1),
  completedAt: null,
  version: 2,
};

const incident = (
  n: string,
  number: string,
  fields: Pick<IncidentListItem, 'type' | 'severity' | 'status' | 'location'> & Partial<IncidentListItem>,
  days: number,
): IncidentListItem => ({
  id: id(n),
  incidentNumber: number,
  isSentinel: false,
  occurredAt: daysAgo(days + 0.1),
  reportedAt: daysAgo(days),
  departmentId: ICU.id,
  departmentName: ICU.name,
  isAnonymous: false,
  assignedToUserId: null,
  openCapas: 0,
  ...fields,
});

const INCIDENTS: IncidentListItem[] = [
  incident(
    'f0f0',
    'INC-26-00118',
    {
      type: 'MedicationError',
      severity: 'Mild',
      status: 'UnderReview',
      location: 'ICU bed 4',
      assignedToUserId: NURSE_MANAGER.id,
      openCapas: 1,
    },
    2.3,
  ),
  incident(
    '1c12',
    'INC-26-00121',
    { type: 'PatientFall', severity: 'Moderate', status: 'Reported', location: 'Ward 2, bathroom' },
    0.4,
  ),
  incident(
    '1c13',
    'INC-26-00097',
    {
      type: 'Clinical',
      severity: 'Severe',
      status: 'Investigation',
      location: 'OT 2',
      isSentinel: true,
      departmentId: null,
      departmentName: null,
    },
    12,
  ),
  incident(
    '1c14',
    'INC-26-00088',
    {
      type: 'NeedleStick',
      severity: 'NoHarm',
      status: 'CapaInProgress',
      location: 'Laboratory',
      isAnonymous: true,
      openCapas: 2,
    },
    20,
  ),
  incident(
    '1c15',
    'INC-26-00061',
    {
      type: 'EquipmentFailure',
      severity: 'NearMiss',
      status: 'Closed',
      location: 'Pharmacy store',
      departmentId: PHARMACY.id,
      departmentName: PHARMACY.name,
    },
    40,
  ),
];

const MY_INCIDENTS: MyIncidentItem[] = [
  {
    id: id('1c12'),
    incidentNumber: 'INC-26-00121',
    type: 'PatientFall',
    severity: 'Moderate',
    status: 'Reported',
    reportedAt: daysAgo(0.4),
  },
  {
    id: id('1c15'),
    incidentNumber: 'INC-26-00061',
    type: 'EquipmentFailure',
    severity: 'NearMiss',
    status: 'Closed',
    reportedAt: daysAgo(40),
  },
];

// ---------------------------------------------------------------------------------------------------- CAPA

/** Owned by the ward sister and open: the quality team may start it, mark it done, edit or cancel it. */
const CAPA: CapaDetail = {
  id: RECORD_ID,
  capaNumber: 'CAPA-26-0041',
  source: 'Incident',
  incidentId: RECORD_ID,
  riskId: null,
  type: 'Preventive',
  action: 'Withdraw old insulin charts from all wards and train ICU nurses on the new sliding scale',
  ownerUserId: NURSE_MANAGER.id,
  ownerName: NURSE_MANAGER.name,
  dueDate: dateIn(10),
  status: 'Open',
  isOverdue: false,
  startedAt: null,
  completedAt: null,
  completionNote: null,
  effectivenessCheckDue: null,
  isEffective: null,
  effectivenessNote: null,
  verifiedAt: null,
  cancelReason: null,
  reworkCount: 0,
  version: 1,
};

const capa = (
  n: string,
  number: string,
  fields: Pick<CapaListItem, 'action' | 'status' | 'dueDate'> & Partial<CapaListItem>,
): CapaListItem => ({
  id: id(n),
  capaNumber: number,
  source: 'Incident',
  incidentId: RECORD_ID,
  incidentNumber: 'INC-26-00118',
  riskId: null,
  type: 'Corrective',
  ownerUserId: NURSE_MANAGER.id,
  ownerName: NURSE_MANAGER.name,
  isOverdue: false,
  effectivenessCheckDue: null,
  ...fields,
});

const CAPAS: CapaListItem[] = [
  capa('f0f0', 'CAPA-26-0041', { action: CAPA.action, status: 'Open', dueDate: dateIn(10), type: 'Preventive' }),
  capa('ca02', 'CAPA-26-0042', {
    action: 'Double-check of insulin doses by a second nurse in ICU',
    status: 'InProgress',
    dueDate: dateIn(14),
  }),
  capa('ca03', 'CAPA-26-0035', {
    action: 'Replace the faulty infusion pump in OT 2',
    status: 'Completed',
    dueDate: dateAgo(5),
    effectivenessCheckDue: dateIn(25),
    incidentId: id('1c13'),
    incidentNumber: 'INC-26-00097',
  }),
  capa('ca04', 'CAPA-26-0029', {
    action: 'Sharps containers at every phlebotomy chair',
    status: 'Open',
    dueDate: dateAgo(3),
    isOverdue: true,
    source: 'Risk',
    incidentId: null,
    incidentNumber: null,
    riskId: RECORD_ID,
  }),
];

/** Actions the signed-in user owns: one to start, one to mark done. */
const MY_CAPAS: CapaListItem[] = [
  capa('ca05', 'CAPA-26-0044', {
    action: 'Audit 20 ICU insulin charts',
    status: 'Open',
    dueDate: dateIn(20),
    ownerUserId: ME,
    ownerName: STAFF_USER.displayName,
  }),
  capa('ca06', 'CAPA-26-0039', {
    action: 'Update the high-alert medicines list',
    status: 'InProgress',
    dueDate: dateIn(7),
    ownerUserId: ME,
    ownerName: STAFF_USER.displayName,
  }),
];

// ---------------------------------------------------------------------------------------------------- risks

const risk = (
  n: string,
  number: string,
  title: string,
  category: RiskListItem['category'],
  likelihood: number,
  impact: number,
  status: RiskListItem['status'],
  reviewIn: number,
): RiskListItem => ({
  id: id(n),
  riskNumber: number,
  title,
  category,
  departmentId: null,
  likelihood,
  impact,
  score: likelihood * impact,
  rating: riskRating(likelihood * impact),
  residualScore: status === 'Mitigating' ? Math.max(1, likelihood * impact - 6) : null,
  residualRating: status === 'Mitigating' ? riskRating(Math.max(1, likelihood * impact - 6)) : null,
  status,
  ownerUserId: NURSE_MANAGER.id,
  reviewDate: dateIn(reviewIn),
  isReviewOverdue: reviewIn < 0,
});

const RISKS: RiskListItem[] = [
  risk('f0f0', 'RSK-0007', 'Needle-stick injuries in phlebotomy', 'PatientSafety', 4, 3, 'Mitigating', -2),
  risk('5c02', 'RSK-0003', 'Fire in the oxygen manifold room', 'FireAndSafety', 2, 5, 'Open', 30),
  risk('5c03', 'RSK-0011', 'Ransomware on the hospital network', 'InformationSecurity', 3, 5, 'Accepted', 60),
  risk('5c04', 'RSK-0002', 'Expired drugs on ward shelves', 'Compliance', 2, 2, 'Closed', 90),
];

const RISK: RiskDetail = {
  ...RISKS[0]!,
  description: 'Staff injuries during blood collection at the OPD collection centre.',
  residualLikelihood: 2,
  residualImpact: 3,
  existingControls: 'Safety-engineered needles at two of five chairs.',
  mitigationPlan: 'Sharps containers at every chair; training for new phlebotomists.',
  ownerName: NURSE_MANAGER.name,
  lastReviewedAt: daysAgo(92),
  lastReviewNote: 'Two injuries this quarter.',
  version: 3,
};

const MATRIX: RiskMatrixCell[] = [1, 2, 3, 4, 5].flatMap((likelihood) =>
  [1, 2, 3, 4, 5].map((impact) => ({
    likelihood,
    impact,
    score: likelihood * impact,
    rating: riskRating(likelihood * impact),
    count: RISKS.filter((r) => r.likelihood === likelihood && r.impact === impact).length,
  })),
);

// ---------------------------------------------------------------------------------------------------- indicators

const lastMonth = new Date(now);
lastMonth.setUTCDate(1);
lastMonth.setUTCMonth(lastMonth.getUTCMonth() - 1);
const monthStart = (back: number) => {
  const d = new Date(lastMonth);
  d.setUTCMonth(d.getUTCMonth() - back);
  return d.toISOString().slice(0, 10);
};
const monthLabel = (back: number) =>
  new Date(`${monthStart(back)}T00:00:00Z`).toLocaleDateString('en-IN', {
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });

const INDICATORS: IndicatorDto[] = [
  {
    id: RECORD_ID,
    code: 'NABH-PSQ-3a',
    name: 'Medication errors per 1,000 patient days',
    category: 'Patient safety',
    numeratorDefinition: 'Medication errors reported',
    denominatorDefinition: 'In-patient days',
    unit: 'PerThousand',
    multiplier: 1000,
    target: 2,
    direction: 'LowerIsBetter',
    frequency: 'Monthly',
    nabhReference: 'PSQ.3',
    sortOrder: 1,
    isActive: true,
    latest: { periodStart: monthStart(0), periodLabel: monthLabel(0), value: 2.4, meetsTarget: false },
    version: 2,
    isComputed: true,
  },
  {
    id: id('1d02'),
    code: 'NABH-PSQ-1',
    name: 'Hand hygiene compliance',
    category: 'Infection control',
    numeratorDefinition: 'Opportunities with hand hygiene',
    denominatorDefinition: 'Opportunities observed',
    unit: 'Percent',
    multiplier: 100,
    target: 90,
    direction: 'HigherIsBetter',
    frequency: 'Monthly',
    nabhReference: 'HIC.4',
    sortOrder: 2,
    isActive: true,
    latest: { periodStart: monthStart(0), periodLabel: monthLabel(0), value: 93.5, meetsTarget: true },
    version: 1,
    isComputed: false,
  },
  {
    id: id('1d03'),
    code: 'NABH-AAC-1',
    name: 'Time for initial assessment in emergency',
    category: 'Access and assessment',
    numeratorDefinition: 'Sum of minutes to initial assessment',
    denominatorDefinition: 'Emergency patients',
    unit: 'Minutes',
    multiplier: 1,
    target: 10,
    direction: 'LowerIsBetter',
    frequency: 'Monthly',
    nabhReference: 'AAC.2',
    sortOrder: 3,
    isActive: true,
    latest: null,
    version: 1,
    isComputed: true,
  },
];

const VALUES: IndicatorValueDto[] = [0, 1, 2].map((back) => ({
  id: id(`1e0${back}`),
  periodStart: monthStart(back),
  periodLabel: monthLabel(back),
  numerator: [12, 9, 8][back]!,
  denominator: 5000,
  value: [2.4, 1.8, 1.6][back]!,
  meetsTarget: back !== 0,
  notes: back === 0 ? 'Two insulin errors in ICU.' : null,
  version: 1,
  isComputed: true,
  computedAt: daysAgo(back * 30 + 2),
}));

/** No points: the chart draws on a canvas, which the test browser (jsdom) has not got, and the page would fail. */
const TREND: IndicatorTrendDto = {
  indicatorId: RECORD_ID,
  code: 'NABH-PSQ-3a',
  name: 'Medication errors per 1,000 patient days',
  unit: 'PerThousand',
  target: 2,
  direction: 'LowerIsBetter',
  points: [],
  latest: 2.4,
  average: 1.93,
  trend: 'Worsening',
};

const SCORECARD: ScorecardRow[] = INDICATORS.map((i) => ({
  indicatorId: i.id,
  code: i.code,
  name: i.name,
  category: i.category,
  unit: i.unit,
  target: i.target,
  direction: i.direction,
  periodStart: monthStart(0),
  periodLabel: monthLabel(0),
  value: i.latest?.value ?? null,
  meetsTarget: i.latest?.meetsTarget ?? null,
}));

// ---------------------------------------------------------------------------------------------------- dashboard

const DASHBOARD: QualityDashboard = {
  from: dateAgo(365),
  to: dateAgo(0),
  totalIncidents: 118,
  sentinelEvents: 2,
  openIncidents: 9,
  byType: [
    { key: 'MedicationError', count: 31 },
    { key: 'PatientFall', count: 22 },
    { key: 'NeedleStick', count: 14 },
    { key: 'Other', count: 51 },
  ],
  bySeverity: [
    { key: 'NearMiss', count: 40 },
    { key: 'NoHarm', count: 45 },
    { key: 'Mild', count: 25 },
    { key: 'Moderate', count: 6 },
    { key: 'Severe', count: 2 },
  ],
  openByStatus: [
    { key: 'Reported', count: 3 },
    { key: 'UnderReview', count: 3 },
    { key: 'Investigation', count: 2 },
    { key: 'CapaInProgress', count: 1 },
  ],
  byMonth: [2, 1, 0].map((back) => ({
    month: monthStart(back).slice(0, 7),
    total: [9, 11, 12][back]!,
    sentinel: back === 1 ? 1 : 0,
  })),
  capas: { open: 6, inProgress: 4, awaitingVerification: 2, overdue: 1, verifiedInPeriod: 23 },
  overdueCapas: [
    {
      id: id('ca04'),
      capaNumber: 'CAPA-26-0029',
      action: 'Sharps containers at every phlebotomy chair',
      ownerName: NURSE_MANAGER.name,
      dueDate: dateAgo(3),
      daysOverdue: 3,
    },
  ],
  topRisks: [
    {
      id: RECORD_ID,
      riskNumber: 'RSK-0007',
      title: 'Needle-stick injuries in phlebotomy',
      score: 12,
      rating: 'High',
      residualScore: 6,
      reviewDate: dateAgo(2),
    },
    {
      id: id('5c03'),
      riskNumber: 'RSK-0011',
      title: 'Ransomware on the hospital network',
      score: 15,
      rating: 'High',
      residualScore: null,
      reviewDate: dateIn(60),
    },
  ],
  risksDueForReview: 1,
  indicatorsOffTarget: [
    {
      id: RECORD_ID,
      code: 'NABH-PSQ-3a',
      name: 'Medication errors per 1,000 patient days',
      periodLabel: monthLabel(0),
      value: 2.4,
      target: 2,
      direction: 'LowerIsBetter',
    },
  ],
};

/** A network outage whose paper records are still being entered, and the year's downtime figures. */
const DOWNTIME: DowntimeEvent[] = [
  {
    id: '0192a000-0000-7000-8000-0000000071d1',
    number: 'NHDT260004',
    kind: 'Unplanned',
    cause: 'NetworkOutage',
    affected: 'All of HC360',
    description: 'Core switch in the server room failed.',
    startedAt: '2026-10-20T04:00:00Z',
    endedAt: '2026-10-20T05:30:00Z',
    minutes: 90,
    status: 'BackEntryPending',
    recordedByName: 'Meena Iyer',
    actionsTaken: 'Switch replaced from spares.',
    endedByName: 'Meena Iyer',
    backEntryCompletedAt: null,
    paperRecords: null,
    backEntryNote: null,
    backEntrySignedByName: null,
    version: 5,
  },
];

const DOWNTIME_SUMMARY: DowntimeSummary = {
  from: '2025-10-28',
  to: '2026-10-27',
  unplanned: 2,
  planned: 1,
  drills: 1,
  unplannedMinutes: 135,
  plannedMinutes: 30,
  longestUnplannedMinutes: 90,
  unplannedAvailabilityPercent: 99.974,
  meanMinutesToRestore: 68,
  meanHoursToBackEntry: 5.5,
  ongoing: 0,
  backEntryPending: 1,
  lastDrillAt: '2026-07-14T05:00:00Z',
};

// ---------------------------------------------------------------------------------------------------- MvPI

/** A failed knee implant reported from an equipment failure: a draft with all MvPI asks for (it can be sent), and one sent. */
const MVPI_DRAFT: MvpiReport = {
  id: RECORD_ID,
  number: 'NHMDAE2600004',
  status: 'Draft',
  reportedOn: dateAgo(1),
  incidentId: id('1c15'),
  incidentNumber: 'INC-26-00061',
  followUpOfId: null,
  followUpOfNumber: null,
  followUpOfReference: null,
  followUps: [],
  patientId: id('1'),
  noPatient: false,
  patientInitials: 'L. N.',
  uhid: 'NH26000611',
  ageYears: 67,
  sex: 'Female',
  weightKg: 64,
  eventOn: dateAgo(2),
  awareOn: dateAgo(2),
  location: 'Ward 3, bed 12',
  description: 'The tibial component of the knee implant loosened six weeks after surgery; revision planned.',
  seriousness: 'Hospitalisation, RequiredIntervention',
  deathOn: null,
  outcome: 'NotRecovered',
  device: {
    ...blankDevice(),
    name: 'Attune',
    genericName: 'Knee implant system',
    riskClass: 'C',
    manufacturer: 'DePuy Synthes',
    lotNumber: 'KN2407',
    serialNumber: 'SN-A1',
    implantable: true,
    implantedOn: dateAgo(44),
    use: 'SingleUse',
    keptAt: 'Biomedical quarantine shelf 2',
  },
  reporterName: STAFF_USER.displayName,
  reporterDesignation: 'Quality manager',
  reporterDepartment: 'Quality',
  submittedOn: null,
  submittedByName: null,
  reference: null,
  referenceCorrectionReason: null,
  referenceCorrectedByName: null,
  referenceCorrectedAt: null,
  missing: {},
  version: 3,
};

const MVPI_REPORTS: MvpiReportListItem[] = [
  {
    id: RECORD_ID,
    number: MVPI_DRAFT.number,
    status: 'Draft',
    reportedOn: MVPI_DRAFT.reportedOn,
    eventOn: MVPI_DRAFT.eventOn,
    patientInitials: 'L. N.',
    uhid: 'NH26000611',
    noPatient: false,
    deviceName: 'Attune',
    manufacturer: 'DePuy Synthes',
    serious: true,
    outcome: 'NotRecovered',
    incidentNumber: 'INC-26-00061',
    followUpOfNumber: null,
    submittedOn: null,
    reference: null,
    reporterName: STAFF_USER.displayName,
  },
  {
    id: id('3d02'),
    number: 'NHMDAE2600002',
    status: 'Submitted',
    reportedOn: dateAgo(30),
    eventOn: dateAgo(31),
    patientInitials: null,
    uhid: null,
    noPatient: true,
    deviceName: 'IntelliVue MX450',
    manufacturer: 'Philips',
    serious: false,
    outcome: 'Unknown',
    incidentNumber: null,
    followUpOfNumber: null,
    submittedOn: dateAgo(29),
    reference: 'MDAE-TN-2026-0042',
    reporterName: STAFF_USER.displayName,
  },
];

// ---------------------------------------------------------------------------------------------------- computed, HAI, audits

const DRILLDOWN: IndicatorDrilldown = {
  indicatorId: RECORD_ID,
  code: 'NABH-PSQ-3a',
  name: 'Medication errors per 1,000 patient days',
  numeratorDefinition: 'Medication errors reported',
  denominatorDefinition: 'In-patient days',
  unit: 'PerThousand',
  periodStart: monthStart(0),
  periodEnd: monthStart(0),
  periodLabel: monthLabel(0),
  numerator: 12,
  denominator: 5000,
  value: 2.4,
  meetsTarget: false,
  stored: VALUES[0]!,
  note: 'Denominator: 5000 in-patient days (midnight census) from 410 stays.',
  unavailable: false,
  recordCount: 1,
  records: [
    {
      role: 'Numerator',
      kind: 'Incident',
      reference: 'INC-26-00118',
      uhid: 'NH26000611',
      at: daysAgo(2.4),
      minutes: null,
      detail: 'Mild, raised from Inpatient',
    },
  ],
};

const DEVICE: DeviceEpisode = {
  id: id('de71'),
  patientId: id('1'),
  uhid: 'NH26000611',
  admissionId: id('ad01'),
  admissionNumber: 'NHIP2600214',
  wardName: ICU.name,
  device: 'CentralLine',
  detail: 'Right internal jugular, triple lumen',
  insertedAt: daysAgo(4),
  insertedByName: 'Sr. Mary Thomas',
  removedAt: null,
  removalReason: null,
  removedByName: null,
  deviceDays: 5,
  notes: null,
  version: 2,
};

const HAI_CASES: HaiCaseListItem[] = [
  {
    id: RECORD_ID,
    caseNumber: 'NHHAI260007',
    type: 'Clabsi',
    origin: 'CultureAlert',
    status: 'Suspected',
    uhid: 'NH26000611',
    eventOn: dateAgo(1),
    organism: 'Klebsiella pneumoniae',
    specimen: 'Blood culture',
    meetsDefinition: false,
    createdAt: daysAgo(0.5),
  },
];

const HAI_RATES: HaiRateRow[] = [0, 1].map((back) => ({
  month: monthStart(back),
  label: monthLabel(back),
  patientDays: 900,
  centralLineDays: 120,
  urinaryCatheterDays: 210,
  ventilatorDays: 60,
  clabsi: back,
  cauti: 0,
  vap: 0,
  ssi: 1,
  operations: 140,
  clabsiRate: back === 0 ? 0 : 8.33,
  cautiRate: 0,
  vapRate: 0,
  ssiRate: 0.71,
  centralLineUtilisation: 0.13,
  urinaryCatheterUtilisation: 0.23,
  ventilatorUtilisation: 0.07,
  suspected: back === 0 ? 1 : 0,
}));

const AUDIT_TEMPLATES: AuditTemplate[] = [
  {
    id: id('a7e1'),
    code: 'HIC-BASIC',
    title: 'Hospital infection control',
    chapter: 'Hic',
    description: null,
    items: [{ key: 'I01', text: 'Hand hygiene facilities are available at the point of care', reference: 'HIC' }],
    isActive: true,
    version: 1,
  },
];

const AUDITS: AuditListItem[] = [
  {
    id: RECORD_ID,
    auditNumber: 'NHAUD260003',
    title: 'Hospital infection control',
    chapter: 'Hic',
    area: ICU.name,
    scheduledOn: dateAgo(2),
    leadAuditorUserId: ME,
    leadAuditorName: STAFF_USER.displayName,
    recurrence: 'Monthly',
    status: 'InProgress',
    isOverdue: true,
    compliancePercent: 85.7,
    nonCompliances: 1,
    openCapas: 0,
  },
];

export const ANSWERS: FakeAnswers = {
  [`GET /api/v1/quality/indicators/${RECORD_ID}/drilldown`]: DRILLDOWN,
  'GET /api/v1/quality/hai/cases': HAI_CASES,
  'GET /api/v1/quality/hai/devices': [DEVICE],
  'GET /api/v1/quality/hai/rates': HAI_RATES,
  'GET /api/v1/quality/audits': AUDITS,
  'GET /api/v1/quality/audits/mine': AUDITS,
  'GET /api/v1/quality/audits/templates': AUDIT_TEMPLATES,
  'GET /api/v1/quality/mvpi': MVPI_REPORTS,
  [`GET /api/v1/quality/mvpi/${RECORD_ID}`]: MVPI_DRAFT,
  'GET /api/v1/quality/downtime': DOWNTIME,
  'GET /api/v1/quality/downtime/summary': DOWNTIME_SUMMARY,
  'GET /api/v1/quality/dashboard': DASHBOARD,
  'GET /api/v1/quality/incidents': paged(INCIDENTS),
  'GET /api/v1/quality/incidents/mine': MY_INCIDENTS,
  [`GET /api/v1/quality/incidents/${RECORD_ID}`]: INCIDENT,
  [`GET /api/v1/quality/incidents/${RECORD_ID}/investigation`]: INVESTIGATION,
  'GET /api/v1/quality/capas': paged(CAPAS),
  'GET /api/v1/quality/capas/mine': MY_CAPAS,
  [`GET /api/v1/quality/capas/${RECORD_ID}`]: CAPA,
  'GET /api/v1/quality/risks': paged(RISKS),
  'GET /api/v1/quality/risks/matrix': MATRIX,
  [`GET /api/v1/quality/risks/${RECORD_ID}`]: RISK,
  'GET /api/v1/quality/indicators': INDICATORS,
  'GET /api/v1/quality/indicators/scorecard': SCORECARD,
  [`GET /api/v1/quality/indicators/${RECORD_ID}/values`]: VALUES,
  [`GET /api/v1/quality/indicators/${RECORD_ID}/trend`]: TREND,
};
