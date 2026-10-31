import type { ApprovalRequestDto } from '@/modules/approvals/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Approvals (maker-checker): a credit note another person asked for, waiting for the signed-in user's decision; a merge
// the user asked for, and a write-off that was approved after its bill changed.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3_600_000).toISOString();
const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

const CREDIT_NOTE: ApprovalRequestDto = {
  id: RECORD_ID,
  kind: 'billing.credit_note',
  title: 'Credit note',
  summary: 'Credit note of ₹1,200.00 on bill NHOPB2600012',
  details: [
    { label: 'Bill', value: 'NHOPB2600012' },
    { label: "Patient's share", value: '₹4,500.00' },
    { label: 'Paid', value: '₹4,500.00' },
    { label: 'Already credited', value: '₹0.00' },
    { label: 'Balance due', value: '₹0.00' },
    { label: 'Credit note', value: '₹1,200.00' },
  ],
  reason: 'Physiotherapy charged at the private room rate',
  status: 'Pending',
  isExpired: false,
  subjectType: 'Bill',
  subjectId: id('b111'),
  patientId: id('1'),
  requestedBy: id('ca5'),
  requestedByName: 'Priya Kulkarni',
  requestedAt: hoursAgo(3),
  expiresAt: new Date(now + 6 * 86_400_000).toISOString(),
  decidedBy: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  outcome: null,
  canDecide: true,
  canWithdraw: false,
  version: 7,
};

const MERGE: ApprovalRequestDto = {
  ...CREDIT_NOTE,
  id: id('a902'),
  kind: 'patients.merge',
  title: 'Patient merge',
  summary: 'Merge NH26000789 into NH24000123',
  details: [
    { label: 'Duplicate, merged away', value: 'NH26000789 · Asha Devi · Female · 54 Y' },
    { label: 'Record kept', value: 'NH24000123 · Asha Devi · Female · 54 Y' },
  ],
  reason: 'Registered twice at the counter, same Aadhaar',
  subjectType: 'Patient',
  subjectId: id('c101'),
  requestedBy: '0192a000-0000-7000-8000-0000000000a1',
  requestedByName: 'Dr. Meera Iyer',
  canDecide: false,
  canWithdraw: true,
  version: 3,
};

const FAILED: ApprovalRequestDto = {
  ...CREDIT_NOTE,
  id: id('a903'),
  kind: 'billing.write_off',
  title: 'Write-off',
  summary: 'Write-off of ₹800.00 on bill NHOPB2600031',
  status: 'Failed',
  decidedBy: id('ac1'),
  decidedByName: 'Ravi Deshmukh',
  decidedAt: hoursAgo(1),
  outcome:
    'Bill NHOPB2600031 changed after this was asked for (a payment, a refund or another credit). Ask again, against the bill as it is now.',
  canDecide: false,
  version: 9,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/approvals': paged([CREDIT_NOTE, MERGE, FAILED]),
  [`GET /api/v1/approvals/${RECORD_ID}`]: CREDIT_NOTE,
};
