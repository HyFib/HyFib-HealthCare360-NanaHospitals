import type { AntimicrobialApproval, StewardshipReport } from '@/modules/ams/api';
import type { FakeAnswers } from '../fakeApi';

// Antimicrobial stewardship: meropenem waiting for approval, and the month's report.

const hoursAgo = (h: number) => new Date(Date.now() - h * 3_600_000).toISOString();

const MEROPENEM: AntimicrobialApproval = {
  id: '0192a000-0000-7000-8000-0000000000a5',
  patientId: '0192a000-0000-7000-8000-0000000000b5',
  patientName: 'Kavitha S',
  uhid: 'NH26000110',
  prescriptionNumber: 'NHRX260000210',
  inpatient: true,
  drugName: 'Meropenem 1 g Injection',
  antimicrobial: 'Meropenem',
  group: 'Watch',
  dose: '1 g',
  route: 'IV',
  durationDays: 5,
  prescribedByName: 'Dr. Arjun Rao',
  prescribedAt: hoursAgo(6),
  site: 'UrinaryTract',
  indication: 'Urosepsis',
  empirical: true,
  cultureSent: true,
  status: 'Pending',
  approvalDueBy: hoursAgo(-18),
  approvalOverdue: false,
  decidedByName: null,
  decidedAt: null,
  approvedDays: null,
  decisionNotes: null,
  organism: null,
  susceptibility: null,
  cultureReleasedAt: null,
  reviewDue: false,
  reviewDecision: null,
  reviewedByName: null,
  reviewedAt: null,
  reviewNotes: null,
  isMine: false,
  version: 1,
};

const REPORT: StewardshipReport = {
  from: '2026-09-25',
  to: '2026-10-25',
  byGroup: [
    { group: 'Access', prescribed: 0, approved: 0, rejected: 0, pending: 0 },
    { group: 'Watch', prescribed: 12, approved: 9, rejected: 2, pending: 1 },
    { group: 'Reserve', prescribed: 2, approved: 2, rejected: 0, pending: 0 },
  ],
  medianHoursToDecision: 3.5,
  decidedLate: 1,
  cultureSentPercent: 82,
  reviewed: 8,
  reviewDecisions: { Continue: 3, DeEscalate: 4, Escalate: 0, SwitchToOral: 1, Stop: 0 },
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/ams/approvals': [MEROPENEM],
  'GET /api/v1/ams/report': REPORT,
};
