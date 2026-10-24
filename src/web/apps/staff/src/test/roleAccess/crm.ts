import type {
  CampaignDelivery,
  CampaignDetail,
  CampaignListItem,
  FollowUpDetail,
  FollowUpListItem,
  LeadDetail,
  LeadListItem,
  Patient360View,
  ReferralStats,
  ReferrerText,
  ReferringDoctorDetail,
  ReferringDoctorListItem,
} from '@/modules/crm/api';
import type { CommunicationEntry } from '@/modules/crm/communications';
import { paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// CRM: a lead at each stage of the pipeline, follow-up calls due, overdue and done, referring doctors with their
// referrals, campaigns in each state (a scheduled one to send back to draft or cancel), and one patient's 360° view.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3600_000).toISOString();
const daysAgo = (days: number) => hoursAgo(days * 24);
const dateAgo = (days: number) => daysAgo(days).slice(0, 10);
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

const ME = STAFF_USER.id;
const CARDIOLOGY = id('de02');

// ---------------------------------------------------------------------------------------------------- leads

const lead = (
  n: string,
  number: string,
  name: string,
  fields: Pick<LeadListItem, 'source' | 'status'> & Partial<LeadListItem>,
): LeadListItem => ({
  id: id(n),
  leadNumber: number,
  name,
  phoneMasked: '98******12',
  interestPackage: null,
  interestDepartmentId: null,
  ownerUserId: ME,
  nextFollowUpAt: null,
  lastContactedAt: null,
  createdAt: daysAgo(3),
  isFollowUpDue: false,
  ...fields,
});

const LEADS: LeadListItem[] = [
  lead('f0f0', 'LD-26-00231', 'Prakash Joshi', {
    source: 'HealthCamp',
    status: 'Qualified',
    interestPackage: 'Executive health check',
    nextFollowUpAt: hoursAgo(-4),
    lastContactedAt: daysAgo(1),
    isFollowUpDue: true,
  }),
  lead('1ead02', 'LD-26-00240', 'Sunita Rao', { source: 'Website', status: 'New', createdAt: hoursAgo(3) }),
  lead('1ead03', 'LD-26-00236', 'Imran Qureshi', {
    source: 'Referral',
    status: 'Contacted',
    interestDepartmentId: CARDIOLOGY,
    lastContactedAt: daysAgo(2),
  }),
  lead('1ead04', 'LD-26-00198', 'Geeta Menon', { source: 'SocialMedia', status: 'Lost', createdAt: daysAgo(30) }),
];

const LEAD: LeadDetail = {
  id: RECORD_ID,
  leadNumber: 'LD-26-00231',
  name: 'Prakash Joshi',
  phone: '9822012345',
  email: 'prakash.joshi@example.in',
  gender: 'Male',
  ageYears: 52,
  city: 'Pune',
  source: 'HealthCamp',
  sourceDetail: 'Kothrud society health camp',
  campaignId: null,
  referringDoctorId: RECORD_ID,
  interestDepartmentId: null,
  interestPackage: 'Executive health check',
  notes: 'Wants a weekend appointment.',
  status: 'Qualified',
  lostReason: null,
  ownerUserId: ME,
  nextFollowUpAt: hoursAgo(-4),
  lastContactedAt: daysAgo(1),
  convertedPatientId: null,
  convertedAppointmentId: null,
  convertedAt: null,
  activities: [
    {
      id: id('1ac1'),
      type: 'Call',
      occurredAt: daysAgo(1),
      outcome: 'Interested',
      notes: 'Asked for the package price (₹4,999).',
      performedByUserId: ME,
    },
    {
      id: id('1ac2'),
      type: 'Sms',
      occurredAt: daysAgo(2),
      outcome: null,
      notes: 'Package brochure sent.',
      performedByUserId: ME,
    },
  ],
  version: 4,
};

// ---------------------------------------------------------------------------------------------------- follow-ups

const followUp = (
  n: string,
  person: { patientId: string; uhid: string; patientName: string },
  fields: Pick<FollowUpListItem, 'type' | 'status' | 'dueDate'> & Partial<FollowUpListItem>,
): FollowUpListItem => ({
  id: id(n),
  ...person,
  phoneMasked: '98******10',
  priority: 'Normal',
  assigneeUserId: ME,
  attempts: 0,
  outcome: null,
  isOverdue: false,
  reminderSentAt: null,
  ...fields,
});

const ASHA = { patientId: id('1'), uhid: 'NH26000123', patientName: 'Asha Devi' };
const RAVI = { patientId: id('2'), uhid: 'NH26000124', patientName: 'Ravi Shankar' };
const LAKSHMI = { patientId: id('3'), uhid: 'NH26000131', patientName: 'Lakshmi Narayanan' };

const FOLLOW_UPS: FollowUpListItem[] = [
  followUp('f0f0', ASHA, { type: 'PostDischarge', status: 'Open', dueDate: dateAgo(0), priority: 'High' }),
  followUp('f002', RAVI, {
    type: 'ChronicReview',
    status: 'InProgress',
    dueDate: dateAgo(2),
    isOverdue: true,
    attempts: 1,
    outcome: 'NotReachable',
  }),
  followUp('f003', LAKSHMI, { type: 'Vaccination', status: 'Open', dueDate: dateAgo(-3), reminderSentAt: hoursAgo(2) }),
  followUp('f004', ASHA, {
    type: 'PostConsultation',
    status: 'Completed',
    dueDate: dateAgo(10),
    attempts: 1,
    outcome: 'AppointmentBooked',
  }),
];

const FOLLOW_UP: FollowUpDetail = {
  ...FOLLOW_UPS[0]!,
  id: RECORD_ID,
  phone: '9876543210',
  visitId: null,
  admissionId: id('ad01'),
  notes: 'Check wound healing and diabetic control after discharge.',
  outcomeNotes: null,
  completedAt: null,
  source: 'Discharge',
  version: 1,
};

// ---------------------------------------------------------------------------------------------------- referrals

const DOCTORS: ReferringDoctorListItem[] = [
  {
    id: RECORD_ID,
    name: 'Dr. Sanjay Kulkarni',
    clinic: 'Kulkarni Clinic',
    specialty: 'General practice',
    city: 'Pune',
    phone: '98******77',
    isActive: true,
    referrals: 42,
    lastReferralAt: daysAgo(1),
  },
  {
    id: id('7d02'),
    name: 'Dr. Ayesha Siddiqui',
    clinic: 'Care Family Clinic',
    specialty: 'Paediatrics',
    city: 'Pimpri',
    phone: null,
    isActive: true,
    referrals: 17,
    lastReferralAt: daysAgo(6),
  },
  {
    id: id('7d03'),
    name: 'Dr. V. Raghavan',
    clinic: null,
    specialty: 'Cardiology',
    city: 'Pune',
    phone: null,
    isActive: false,
    referrals: 3,
    lastReferralAt: daysAgo(200),
  },
];

const DOCTOR: ReferringDoctorDetail = {
  id: RECORD_ID,
  name: 'Dr. Sanjay Kulkarni',
  clinic: 'Kulkarni Clinic',
  phone: '9822077777',
  email: 'kulkarni.clinic@example.in',
  specialty: 'General practice',
  city: 'Pune',
  registrationNumber: 'MMC-2004-11823',
  alternateNames: ['Dr. S. Kulkarni', 'Kulkarni sir'],
  notes: null,
  isActive: true,
  version: 3,
};

const STATS: ReferralStats = {
  doctorId: RECORD_ID,
  from: dateAgo(365),
  to: dateAgo(0),
  referrals: 42,
  lastReferralAt: daysAgo(1),
  byMonth: [
    { label: 'Aug', count: 4 },
    { label: 'Sep', count: 6 },
    { label: 'Oct', count: 3 },
  ],
  byDepartment: [
    { label: 'General medicine', count: 28 },
    { label: 'Cardiology', count: 14 },
  ],
  byVisitType: [
    { label: 'OPD', count: 37 },
    { label: 'Admission', count: 5 },
  ],
};

const UNMATCHED: ReferrerText[] = [
  { text: 'Dr Kulkarny', visits: 3, lastVisitAt: daysAgo(4) },
  { text: 'Shah Nursing Home', visits: 2, lastVisitAt: daysAgo(9) },
];

// ---------------------------------------------------------------------------------------------------- campaigns

const SEGMENT = {
  minAge: 40,
  maxAge: 70,
  gender: null,
  city: 'Pune',
  lastVisitFrom: dateAgo(365),
  lastVisitTo: null,
  departmentIds: [CARDIOLOGY],
};

/** Scheduled for tomorrow morning: it can go back to draft or be cancelled. */
const CAMPAIGN: CampaignDetail = {
  id: RECORD_ID,
  name: 'World Heart Day: free ECG camp',
  channel: 'Sms',
  templateKey: 'campaign.generic',
  message: 'Free ECG and BP check at Nana Hospitals on Sunday, 9 am to 1 pm. Call 020 2545 6789 to book.',
  status: 'Scheduled',
  segment: SEGMENT,
  scheduledAt: hoursAgo(-20),
  startedAt: null,
  completedAt: null,
  cancelReason: null,
  audienceCount: 1840,
  queuedCount: 0,
  skippedCount: 0,
  version: 3,
};

const CAMPAIGNS: CampaignListItem[] = [
  {
    id: RECORD_ID,
    name: CAMPAIGN.name,
    channel: 'Sms',
    status: 'Scheduled',
    scheduledAt: CAMPAIGN.scheduledAt,
    audienceCount: 1840,
    queuedCount: 0,
    skippedCount: 0,
    createdAt: daysAgo(2),
  },
  {
    id: id('ca9002'),
    name: 'Diabetes review reminder',
    channel: 'WhatsApp',
    status: 'Draft',
    scheduledAt: null,
    audienceCount: 0,
    queuedCount: 0,
    skippedCount: 0,
    createdAt: hoursAgo(5),
  },
  {
    id: id('ca9003'),
    name: 'Monsoon dengue awareness',
    channel: 'Sms',
    status: 'Sending',
    scheduledAt: hoursAgo(1),
    audienceCount: 5200,
    queuedCount: 3100,
    skippedCount: 40,
    createdAt: daysAgo(4),
  },
  {
    id: id('ca9004'),
    name: 'Senior citizen health check',
    channel: 'Sms',
    status: 'Completed',
    scheduledAt: daysAgo(20),
    audienceCount: 960,
    queuedCount: 930,
    skippedCount: 30,
    createdAt: daysAgo(25),
  },
];

const DELIVERY: CampaignDelivery = { recipients: 1840, queued: 0, sent: 0, failed: 0, skipped: 0, optedOut: 0 };

// ---------------------------------------------------------------------------------------------------- patient 360

const PATIENT_360: Patient360View = {
  patient: {
    id: RECORD_ID,
    uhid: 'NH26000123',
    fullName: 'Asha Devi',
    gender: 'Female',
    age: '39Y',
    phoneMasked: '98******10',
    city: 'Pune',
    category: 'General',
    preferredLanguage: 'mr',
    isVip: false,
    isDeceased: false,
    registeredAt: daysAgo(400),
  },
  visits: {
    total: 14,
    lastTwelveMonths: 6,
    firstVisitAt: daysAgo(400),
    lastVisitAt: daysAgo(3),
    lastReferredBy: 'Dr. Sanjay Kulkarni',
    byType: [
      { label: 'OPD', count: 12 },
      { label: 'Admission', count: 2 },
    ],
    topDepartments: [
      { label: 'General medicine', count: 9 },
      { label: 'Cardiology', count: 5 },
    ],
  },
  spend: {
    totalBilled: 86_450,
    totalPaid: 84_950,
    billedLastTwelveMonths: 52_300,
    bills: 21,
    lastBilledAt: daysAgo(3),
  },
  feedback: {
    responses: 3,
    averageRating: 4.3,
    lastNps: 9,
    lastSentiment: 'Positive',
    lowScores: 0,
    lastSubmittedAt: daysAgo(3),
  },
  recentComplaints: [
    {
      id: id('c901'),
      complaintNumber: 'CMP-26-00088',
      status: 'Resolved',
      priority: 'Normal',
      category: 'Billing',
      raisedAt: daysAgo(40),
    },
  ],
  openFollowUps: [
    { id: id('f0f0'), type: 'PostDischarge', dueDate: dateAgo(0), status: 'Open', priority: 'High', isOverdue: false },
  ],
  communication: {
    marketingConsent: true,
    updatesConsent: true,
    marketingChangedAt: daysAgo(400),
    campaignsReceived: 4,
    lastCampaignAt: daysAgo(20),
    optedOutByLink: false,
  },
  sourceLead: {
    id: id('1ead09'),
    leadNumber: 'LD-25-01120',
    source: 'HealthCamp',
    sourceDetail: 'Kothrud society health camp',
    createdAt: daysAgo(410),
    convertedAt: daysAgo(400),
  },
};

const COMMUNICATIONS: CommunicationEntry[] = [
  {
    id: id('c0a1'),
    kind: 'Message',
    occurredAt: daysAgo(3),
    channel: 'WhatsApp',
    templateKey: 'lab.report_ready',
    purpose: 'Lab report ready',
    recipient: '98******10',
    status: 'Delivered',
    sentAt: daysAgo(3),
    deliveredAt: daysAgo(3),
    campaignId: null,
    campaignName: null,
    skipReason: null,
    followUpId: null,
    followUpType: null,
    followUpStatus: null,
    callOutcome: null,
    callAttempts: null,
  },
  {
    id: id('c0a2'),
    kind: 'Campaign',
    occurredAt: daysAgo(20),
    channel: 'Sms',
    templateKey: 'campaign.generic',
    purpose: null,
    recipient: '98******10',
    status: 'Sent',
    sentAt: daysAgo(20),
    deliveredAt: null,
    campaignId: id('ca9004'),
    campaignName: 'Senior citizen health check',
    skipReason: null,
    followUpId: null,
    followUpType: null,
    followUpStatus: null,
    callOutcome: null,
    callAttempts: null,
  },
  {
    id: id('c0a3'),
    kind: 'FollowUpCall',
    occurredAt: daysAgo(10),
    channel: null,
    templateKey: null,
    purpose: null,
    recipient: null,
    status: null,
    sentAt: null,
    deliveredAt: null,
    campaignId: null,
    campaignName: null,
    skipReason: null,
    followUpId: id('f004'),
    followUpType: 'PostConsultation',
    followUpStatus: 'Completed',
    callOutcome: 'AppointmentBooked',
    callAttempts: 1,
  },
];

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/crm/leads': paged(LEADS),
  [`GET /api/v1/crm/leads/${RECORD_ID}`]: LEAD,
  'GET /api/v1/crm/follow-ups': paged(FOLLOW_UPS),
  [`GET /api/v1/crm/follow-ups/${RECORD_ID}`]: FOLLOW_UP,
  'GET /api/v1/crm/referring-doctors': paged(DOCTORS),
  [`GET /api/v1/crm/referring-doctors/${RECORD_ID}`]: DOCTOR,
  [`GET /api/v1/crm/referring-doctors/${RECORD_ID}/stats`]: STATS,
  'GET /api/v1/crm/referrals/unmatched': UNMATCHED,
  'GET /api/v1/crm/campaigns': paged(CAMPAIGNS),
  [`GET /api/v1/crm/campaigns/${RECORD_ID}`]: CAMPAIGN,
  [`GET /api/v1/crm/campaigns/${RECORD_ID}/delivery`]: DELIVERY,
  [`GET /api/v1/crm/patients/${RECORD_ID}/360`]: PATIENT_360,
  [`GET /api/v1/crm/patients/${RECORD_ID}/communications`]: paged(COMMUNICATIONS),
};
