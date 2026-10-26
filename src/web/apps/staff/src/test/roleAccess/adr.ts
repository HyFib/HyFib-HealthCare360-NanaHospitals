import type { AdrReportListItem } from '@/modules/pharmacy/adr';
import type { FakeAnswers } from '../fakeApi';

// Adverse drug reactions: a serious draft (so assessing and sending it is offered) and one already sent.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const dateAgo = (days: number) => new Date(Date.now() - days * 86_400_000).toISOString().slice(0, 10);

const REPORTS: AdrReportListItem[] = [
  {
    id: id('a1'),
    number: 'NHADR2600014',
    patientInitials: 'L. N.',
    uhid: 'NH26000611',
    reaction: 'Generalised urticaria and wheeze 20 minutes after the second dose',
    suspectedDrugs: 'Ceftriaxone 1 g Injection',
    serious: true,
    status: 'Draft',
    learnedOn: dateAgo(2),
    dueBy: dateAgo(-13),
    overdue: false,
    reporterName: 'Sr. Kavitha Nair',
    causality: null,
  },
  {
    id: id('a2'),
    number: 'NHADR2600011',
    patientInitials: 'R. S.',
    uhid: 'NH26000124',
    reaction: 'Maculopapular rash on the trunk on day 9',
    suspectedDrugs: 'Phenytoin 100 mg Tablet',
    serious: false,
    status: 'Submitted',
    learnedOn: dateAgo(20),
    dueBy: null,
    overdue: false,
    reporterName: 'Dr. Anil Rao',
    causality: 'Probable',
  },
];

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/pharmacy/adr': REPORTS,
};
