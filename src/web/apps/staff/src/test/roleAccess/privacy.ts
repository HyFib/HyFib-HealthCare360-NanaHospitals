import type {
  AffectedPatientItem,
  BreachDetail,
  BreachListItem,
  CertInContact,
  DataRequestDetail,
  DataRequestListItem,
  NomineeDto,
  PrivacyOfficer,
  PrivacyOverview,
  PrivacySettingsView,
  RetentionReview,
  RetentionRuleDto,
  RetentionRunDto,
  RetentionRunResult,
} from '@/modules/privacy/api';
import { paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Privacy (DPDP Act): a correction request just received with changes to decide, requests in each state, a breach
// the Board has been told about with people still to notify and CERT-In not yet told, the retention schedule with its
// clean-up runs, and the privacy contact.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3600_000).toISOString();
const daysAgo = (days: number) => hoursAgo(days * 24);
const dateIn = (days: number) => daysAgo(-days).slice(0, 10);
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

/** Asha Devi: her patient record is answered by the billing screens' data. */
const ASHA = { patientId: id('1'), uhid: 'NH26000123', principalName: 'Asha Devi' };
const OFFICER = { id: id('a8'), displayName: 'Neha Kulkarni (Data Protection Officer)' };

// ---------------------------------------------------------------------------------------------------- requests

const OVERVIEW: PrivacyOverview = {
  open: 4,
  overdue: 1,
  dueThisWeek: 2,
  awaitingIdentityCheck: 2,
  openBreaches: 1,
  breachReportsOverdue: 0,
};

/** Just received at the front desk, identity not yet checked, two changes to decide: every action is offered. */
const REQUEST: DataRequestDetail = {
  id: RECORD_ID,
  requestNumber: 'DPR-26-00017',
  type: 'Correction',
  status: 'Submitted',
  channel: 'FrontDesk',
  ...ASHA,
  contactPhone: '9876543210',
  contactEmail: null,
  requestedBy: 'Self',
  requesterName: null,
  details: 'My date of birth and address are wrong on my registration.',
  corrections: [
    {
      index: 0,
      field: 'dateOfBirth',
      fieldLabel: 'Date of birth',
      currentValue: '14 Mar 1987',
      requestedValue: '4 Mar 1987',
      decision: 'Pending',
      note: null,
    },
    {
      index: 1,
      field: 'address',
      fieldLabel: 'Address',
      currentValue: '12 MG Road, Pune',
      requestedValue: 'Flat 4, Shanti Apartments, Kothrud, Pune 411038',
      decision: 'Pending',
      note: null,
    },
  ],
  receivedAt: hoursAgo(5),
  dueAt: daysAgo(-29.8),
  isOverdue: false,
  identityCheck: null,
  identityNote: null,
  identityVerifiedAt: null,
  identityVerifiedBy: null,
  assignedToUserId: null,
  assignedToName: null,
  response: null,
  closedAt: null,
  closedBy: null,
  erasedItems: [],
  retainedUntil: null,
  retainedPermanently: false,
  hasSummary: false,
  events: [{ at: hoursAgo(5), kind: 'Received', text: 'Received at the front desk', by: 'Front desk', isPublic: true }],
  version: 1,
};

const listItem = (
  n: string,
  number: string,
  fields: Pick<DataRequestListItem, 'type' | 'status' | 'principalName'> & Partial<DataRequestListItem>,
  receivedDaysAgo: number,
): DataRequestListItem => ({
  id: id(n),
  requestNumber: number,
  channel: 'Portal',
  patientId: null,
  uhid: null,
  requestedBy: 'Self',
  receivedAt: daysAgo(receivedDaysAgo),
  dueAt: daysAgo(receivedDaysAgo - 30),
  isOverdue: receivedDaysAgo > 30,
  identityVerified: true,
  assignedToUserId: OFFICER.id,
  assignedToName: OFFICER.displayName,
  closedAt: null,
  ...fields,
});

const REQUESTS: DataRequestListItem[] = [
  listItem(
    'f0f0',
    'DPR-26-00017',
    {
      type: 'Correction',
      status: 'Submitted',
      ...ASHA,
      channel: 'FrontDesk',
      identityVerified: false,
      assignedToUserId: null,
      assignedToName: null,
    },
    0.2,
  ),
  listItem(
    'd002',
    'DPR-26-00015',
    { type: 'Access', status: 'InProgress', principalName: 'Ravi Shankar', patientId: id('2'), uhid: 'NH26000124' },
    6,
  ),
  listItem(
    'd003',
    'DPR-26-00011',
    {
      type: 'Erasure',
      status: 'InProgress',
      principalName: 'Fatima Sheikh',
      patientId: id('4'),
      uhid: 'NH26000140',
      requestedBy: 'Nominee',
    },
    33,
  ),
  listItem(
    'd004',
    'DPR-26-00009',
    { type: 'Grievance', status: 'Completed', principalName: 'Mohan Lal', closedAt: daysAgo(20) },
    40,
  ),
];

const OFFICERS: PrivacyOfficer[] = [OFFICER, { id: STAFF_USER.id, displayName: STAFF_USER.displayName }];

const NOMINEE: NomineeDto = {
  name: 'Ramesh Devi',
  relationship: 'Husband',
  phone: '9876501234',
  email: null,
  recordedVia: 'FrontDesk',
  updatedAt: daysAgo(200),
  version: 1,
};

// ---------------------------------------------------------------------------------------------------- breaches

/**
 * The Board told yesterday; the detailed report, an extension, the CERT-In report and closing are next, and two of the
 * three people listed still have to be sent the notice.
 */
const BREACH: BreachDetail = {
  id: RECORD_ID,
  breachNumber: 'BR-26-0003',
  title: 'Lab reports e-mailed to the wrong patient',
  description: 'A batch of 3 lab reports was e-mailed to one patient because of a mail-merge error in the lab system.',
  status: 'BoardInformed',
  awareAt: hoursAgo(30),
  occurredFrom: hoursAgo(34),
  occurredTo: hoursAgo(34),
  nature: ['Confidentiality'],
  location: 'Laboratory information system',
  dataCategories: ['Identity', 'Health'],
  estimatedAffected: 3,
  consequences: 'Others could read the test results.',
  mitigation: 'The recipient confirmed deletion; mail merge switched off.',
  principalSteps: 'Nothing is needed from you; call us if you received an e-mail meant for someone else.',
  cause: 'A template change merged three report attachments into one e-mail.',
  findings: 'One recipient, three reports, no further sharing.',
  remediation: 'Second check before bulk e-mails; vendor fix due next week.',
  principalNotice: 'Your lab report was sent to another patient by mistake on Tuesday.',
  boardInformedAt: hoursAgo(26),
  reportDueAt: hoursAgo(-42),
  extendedUntil: null,
  reportSubmittedAt: null,
  boardReference: 'DPB/2026/INT/0412',
  closedAt: null,
  closingNote: null,
  notifiedAt: hoursAgo(20),
  reportOverdue: false,
  affectedListed: 3,
  notified: 1,
  withoutContact: 0,
  missingForReport: [],
  missingForNotice: [],
  version: 4,
  certInType: 'DataLeak',
  certInReportDueAt: hoursAgo(24),
  certInReportedAt: null,
  certInReference: null,
  certInReportOverdue: true,
};

const BREACHES: BreachListItem[] = [
  {
    id: RECORD_ID,
    breachNumber: BREACH.breachNumber,
    title: BREACH.title,
    status: BREACH.status,
    awareAt: BREACH.awareAt,
    reportDueAt: BREACH.reportDueAt,
    reportOverdue: false,
    estimatedAffected: 3,
    affectedListed: 3,
    notified: 1,
    boardInformedAt: BREACH.boardInformedAt,
    reportSubmittedAt: null,
    certInType: 'DataLeak',
    certInReportDueAt: BREACH.certInReportDueAt,
    certInReportedAt: null,
    certInReportOverdue: true,
  },
  {
    id: id('b2e2'),
    breachNumber: 'BR-26-0001',
    title: 'Stolen laptop from the billing office',
    status: 'Closed',
    awareAt: daysAgo(90),
    reportDueAt: daysAgo(87),
    reportOverdue: false,
    estimatedAffected: 1200,
    affectedListed: 1180,
    notified: 1180,
    boardInformedAt: daysAgo(90),
    reportSubmittedAt: daysAgo(88),
    certInType: null,
    certInReportDueAt: null,
    certInReportedAt: null,
    certInReportOverdue: false,
  },
];

const AFFECTED: AffectedPatientItem[] = [
  {
    patientId: id('2'),
    uhid: 'NH26000124',
    fullName: 'Ravi Shankar',
    phone: '97******21',
    hasEmail: true,
    addedAt: hoursAgo(25),
    notifiedAt: hoursAgo(20),
    noticeChannels: 'sms,email',
  },
  {
    patientId: id('3'),
    uhid: 'NH26000131',
    fullName: 'Lakshmi Narayanan',
    phone: '99******40',
    hasEmail: false,
    addedAt: hoursAgo(25),
    notifiedAt: null,
    noticeChannels: null,
  },
  {
    patientId: id('5'),
    uhid: 'NH26000152',
    fullName: 'Suresh Patil',
    phone: null,
    hasEmail: true,
    addedAt: hoursAgo(25),
    notifiedAt: null,
    noticeChannels: null,
  },
];

// ---------------------------------------------------------------------------------------------------- retention

const rule = (
  key: string,
  title: string,
  action: RetentionRuleDto['action'],
  unit: RetentionRuleDto['unit'],
  value: number | null,
  more: Partial<RetentionRuleDto> = {},
): RetentionRuleDto => ({
  key,
  title,
  description: `${title} older than this are removed.`,
  basis: 'Hospital policy',
  action,
  unit,
  value,
  defaultValue: value,
  minimumValue: unit === 'Days' ? 30 : 1,
  maximumValue: unit === 'Days' ? 3650 : 50,
  editable: true,
  lastRemoved: 0,
  lastRunAt: hoursAgo(20),
  version: null,
  ...more,
});

const RETENTION_RULES: RetentionRuleDto[] = [
  rule('messages.log', 'Message log', 'Delete', 'Days', 180, { lastRemoved: 1240, version: 2 }),
  rule('signin.log', 'Sign-in records', 'Delete', 'Days', 365),
  rule('records.outpatient', 'Out-patient medical records', 'Review', 'Years', 3, {
    basis: 'MCI regulations 1.3.1: at least 3 years',
    editable: false,
  }),
  rule('records.mlc', 'Medico-legal records', 'Keep', 'Years', null, {
    defaultValue: null,
    editable: false,
    basis: 'Kept permanently',
  }),
];

const RESULTS: RetentionRunResult[] = [
  { key: 'messages.log', removed: 1240, before: daysAgo(180) },
  { key: 'signin.log', removed: 312, before: daysAgo(365) },
];

const RETENTION_RUNS: RetentionRunDto[] = [
  {
    id: id('7a01'),
    startedAt: hoursAgo(20),
    finishedAt: hoursAgo(19.9),
    trigger: 'Scheduled',
    startedByUserId: null,
    startedByName: null,
    totalRemoved: 1552,
    results: RESULTS,
    error: null,
  },
  {
    id: id('7a02'),
    startedAt: daysAgo(8),
    finishedAt: daysAgo(8),
    trigger: 'Manual',
    startedByUserId: STAFF_USER.id,
    startedByName: STAFF_USER.displayName,
    totalRemoved: 0,
    results: [],
    error: 'The database was busy; try again later.',
  },
];

const RETENTION_REVIEW: RetentionReview = {
  outpatientChartsDue: 412,
  inpatientChartsDue: 37,
  erasureHolds: [
    {
      requestId: id('d003'),
      requestNumber: 'DPR-26-00011',
      patientId: id('4'),
      uhid: 'NH26000140',
      principalName: 'Fatima Sheikh',
      retainedUntil: dateIn(400),
      retainedPermanently: false,
      isDue: false,
    },
  ],
};

const CONTACT = {
  title: 'Data Protection Officer',
  name: 'Neha Kulkarni',
  email: 'privacy@nanahospitals.in',
  phone: '020 2545 6700',
  address: '12 Karve Road, Erandwane, Pune 411004',
};

const SETTINGS: PrivacySettingsView = {
  settings: { contact: CONTACT, responseDays: 30, version: 2, responseDaysVersion: 1 },
  published: CONTACT,
};

const CERT_IN: CertInContact = {
  name: 'Priya Raman',
  designation: 'Chief Information Security Officer',
  email: 'ciso@nanahospitals.in',
  phone: '+919876543210',
  address: null,
  version: 1,
};

export const ANSWERS: FakeAnswers = {
  [`GET /api/v1/privacy/patients/${RECORD_ID}/nominee`]: { nominee: NOMINEE },
  'GET /api/v1/privacy/overview': OVERVIEW,
  'GET /api/v1/privacy/requests': paged(REQUESTS),
  [`GET /api/v1/privacy/requests/${RECORD_ID}`]: REQUEST,
  'GET /api/v1/privacy/officers': OFFICERS,
  [`GET /api/v1/privacy/patients/${ASHA.patientId}/nominee`]: { nominee: NOMINEE },
  'GET /api/v1/privacy/breaches': paged(BREACHES),
  [`GET /api/v1/privacy/breaches/${RECORD_ID}`]: BREACH,
  [`GET /api/v1/privacy/breaches/${RECORD_ID}/affected`]: paged(AFFECTED),
  'GET /api/v1/privacy/retention': RETENTION_RULES,
  'GET /api/v1/privacy/retention/runs': RETENTION_RUNS,
  'GET /api/v1/privacy/retention/preview': RESULTS,
  'GET /api/v1/privacy/retention/review': RETENTION_REVIEW,
  'GET /api/v1/privacy/settings': SETTINGS,
  'GET /api/v1/privacy/settings/cert-in': CERT_IN,
};
