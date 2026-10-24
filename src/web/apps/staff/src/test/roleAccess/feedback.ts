import type {
  ComplaintDetail,
  ComplaintListItem,
  FeedbackAnalytics,
  FeedbackFormDto,
  FeedbackResponseDetail,
  FeedbackResponseListItem,
} from '@/modules/feedback/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Patient feedback: OP, IP and general forms with their QR links, responses from each channel (one low score that
// raised a complaint), complaints open, in progress and resolved, and the month's analytics.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3600_000).toISOString();
const daysAgo = (days: number) => hoursAgo(days * 24);
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

const OPD = '0192a000-0000-7000-8000-00000000de01';
const WARD_NURSE = id('a3');

// ---------------------------------------------------------------------------------------------------- forms

const form = (
  formId: string,
  code: string,
  title: string,
  type: FeedbackFormDto['type'],
  aspects: FeedbackFormDto['aspects'],
  more: Partial<FeedbackFormDto> = {},
): FeedbackFormDto => ({
  id: formId,
  code,
  title,
  type,
  aspects,
  includesNps: true,
  allowsComment: true,
  introText: 'Tell us about your visit today. It takes a minute.',
  thankYouText: 'Thank you! Your feedback helps us improve.',
  departmentId: null,
  publicToken: `tok-${code.toLowerCase()}`,
  qrUrl: `https://hc360.local/f/tok-${code.toLowerCase()}`,
  isActive: true,
  version: 1,
  ...more,
});

const FORMS: FeedbackFormDto[] = [
  form(RECORD_ID, 'OPD', 'OPD visit feedback', 'OP', ['Doctor', 'FrontDesk', 'Billing', 'Cleanliness', 'Overall'], {
    departmentId: OPD,
    version: 3,
  }),
  form(id('f0e2'), 'IPD', 'Stay feedback (at discharge)', 'IP', [
    'Doctor',
    'Nursing',
    'Food',
    'Cleanliness',
    'Billing',
    'Overall',
  ]),
  form(id('f0e3'), 'GEN', 'General suggestions', 'General', ['Overall'], { includesNps: false }),
  form(id('f0e4'), 'PORTAL', 'Online appointment feedback', 'Portal', ['Overall'], { isActive: false, qrUrl: null }),
];

// ---------------------------------------------------------------------------------------------------- responses

const response = (
  n: string,
  hours: number,
  channel: FeedbackResponseListItem['channel'],
  averageRating: number | null,
  nps: number | null,
  sentiment: FeedbackResponseListItem['sentiment'],
  more: Partial<FeedbackResponseListItem> = {},
): FeedbackResponseListItem => ({
  id: id(n),
  submittedAt: hoursAgo(hours),
  formId: RECORD_ID,
  formTitle: 'OPD visit feedback',
  channel,
  departmentId: OPD,
  averageRating,
  nps,
  sentiment,
  isLowScore: false,
  hasComment: false,
  isAnonymous: channel === 'QrCode' || channel === 'Kiosk',
  complaintNumber: null,
  ...more,
});

const RESPONSES: FeedbackResponseListItem[] = [
  response('fe51', 1, 'SmsLink', 2, 3, 'Negative', {
    isLowScore: true,
    hasComment: true,
    complaintNumber: 'CMP-26-00102',
  }),
  response('fe52', 3, 'QrCode', 4.6, 10, 'Positive', { hasComment: true }),
  response('fe53', 26, 'Kiosk', 3.5, 7, 'Neutral'),
  response('fe54', 50, 'Staff', 4.2, 9, 'Positive', {
    formId: id('f0e2'),
    formTitle: 'Stay feedback (at discharge)',
    departmentId: null,
  }),
];

const RESPONSE: FeedbackResponseDetail = {
  id: id('fe51'),
  formId: RECORD_ID,
  formTitle: 'OPD visit feedback',
  channel: 'SmsLink',
  submittedAt: hoursAgo(1),
  patientId: id('1'),
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  visitId: id('b01'),
  admissionId: null,
  departmentId: OPD,
  ratings: { doctor: 4, frontDesk: 1, billing: 2, cleanliness: 3, overall: 2 },
  nps: 3,
  comment: 'Waited 2 hours at billing; the counter staff were rude.',
  sentiment: 'Negative',
  sentimentScore: -0.7,
  isLowScore: true,
  wantsCallback: true,
  contactPhone: '9876543210',
  complaintId: RECORD_ID,
  complaintNumber: 'CMP-26-00102',
};

// ---------------------------------------------------------------------------------------------------- complaints

/** Raised from the low score an hour ago, owned by the ward sister: it can be taken over, started or resolved. */
const COMPLAINT: ComplaintDetail = {
  id: RECORD_ID,
  complaintNumber: 'CMP-26-00102',
  source: 'Feedback',
  status: 'Open',
  priority: 'High',
  category: 'Billing',
  description: 'Waited 2 hours at billing; the counter staff were rude.',
  departmentId: OPD,
  patientId: id('1'),
  feedbackResponseId: id('fe51'),
  contactName: 'Asha Devi',
  contactPhone: '9876543210',
  ownerUserId: WARD_NURSE,
  resolution: null,
  raisedAt: hoursAgo(1),
  dueAt: hoursAgo(-47),
  resolvedAt: null,
  closedAt: null,
  reopenCount: 0,
  isOverdue: false,
  escalatedAt: null,
  version: 1,
};

const complaint = (
  n: string,
  number: string,
  fields: Pick<ComplaintListItem, 'status' | 'source' | 'category'> & Partial<ComplaintListItem>,
  hours: number,
): ComplaintListItem => ({
  id: id(n),
  complaintNumber: number,
  priority: 'Normal',
  departmentId: OPD,
  patientId: null,
  ownerUserId: WARD_NURSE,
  raisedAt: hoursAgo(hours),
  dueAt: hoursAgo(hours - 48),
  isOverdue: hours > 48,
  turnaroundHours: null,
  escalatedAt: null,
  ...fields,
});

const COMPLAINTS: ComplaintListItem[] = [
  complaint(
    'f0f0',
    'CMP-26-00102',
    { status: 'Open', source: 'Feedback', category: 'Billing', priority: 'High', patientId: id('1') },
    1,
  ),
  complaint(
    'c902',
    'CMP-26-00097',
    { status: 'InProgress', source: 'WalkIn', category: 'Waiting time', ownerUserId: null },
    20,
  ),
  complaint(
    'c903',
    'CMP-26-00090',
    { status: 'Open', source: 'Phone', category: 'Housekeeping', escalatedAt: hoursAgo(10) },
    60,
  ),
  complaint(
    'c904',
    'CMP-26-00088',
    { status: 'Resolved', source: 'Email', category: 'Billing', turnaroundHours: 30, patientId: id('1') },
    24 * 40,
  ),
];

// ---------------------------------------------------------------------------------------------------- analytics

const month = (back: number) => {
  const d = new Date(now);
  d.setUTCDate(1);
  d.setUTCMonth(d.getUTCMonth() - back);
  return d.toISOString().slice(0, 7);
};

const ANALYTICS: FeedbackAnalytics = {
  from: daysAgo(90).slice(0, 10),
  to: daysAgo(0).slice(0, 10),
  responses: 1284,
  nps: { responses: 1102, promoters: 640, passives: 310, detractors: 152, score: 44 },
  averageRating: 4.2,
  lowScores: 61,
  aspects: [
    { aspect: 'Doctor', responses: 1180, average: 4.5 },
    { aspect: 'Nursing', responses: 402, average: 4.3 },
    { aspect: 'Billing', responses: 1120, average: 3.4 },
    { aspect: 'Food', responses: 380, average: 2.9 },
  ],
  bySentiment: [
    { label: 'Positive', count: 902 },
    { label: 'Neutral', count: 290 },
    { label: 'Negative', count: 92 },
  ],
  byChannel: [
    { label: 'QrCode', count: 610 },
    { label: 'SmsLink', count: 420 },
    { label: 'Kiosk', count: 180 },
    { label: 'Staff', count: 74 },
  ],
  trend: [2, 1, 0].map((back) => ({
    month: month(back),
    responses: [402, 431, 451][back]!,
    nps: [41, 45, 46][back]!,
    averageRating: [4.1, 4.2, 4.2][back]!,
    lowScores: [22, 20, 19][back]!,
  })),
  departments: [
    { departmentId: OPD, department: 'General medicine', responses: 820, averageRating: 4.1, nps: 38 },
    { departmentId: id('de02'), department: 'Cardiology', responses: 310, averageRating: 4.5, nps: 61 },
    { departmentId: null, department: 'Not linked to a department', responses: 154, averageRating: 4.0, nps: 30 },
  ],
  complaints: {
    raised: 48,
    open: 6,
    overdue: 1,
    resolved: 42,
    averageTurnaroundHours: 26,
    resolvedWithinSlaPercent: 88,
  },
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/feedback/forms': FORMS,
  [`GET /api/v1/feedback/forms/${RECORD_ID}`]: FORMS[0],
  'GET /api/v1/feedback/responses': paged(RESPONSES),
  [`GET /api/v1/feedback/responses/${RESPONSE.id}`]: RESPONSE,
  'GET /api/v1/feedback/analytics': ANALYTICS,
  'GET /api/v1/feedback/complaints': paged(COMPLAINTS),
  [`GET /api/v1/feedback/complaints/${RECORD_ID}`]: COMPLAINT,
};
