import { todayIso } from '@hc360/core';
import type { IdspCaseRow, IdspOverview, IdspWeekSummary } from '@/modules/mrd/idsp';
import type { FakeAnswers } from '../fakeApi';

// Notifiable diseases (IDSP): this week, still open, and the week before, reported on its P form but overdue on its L
// form; one outbreak-prone case, open, so that discarding it is offered.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const daysAgo = (days: number) => new Date(Date.now() - days * 86_400_000).toISOString();

const week = (offset: number, partial: Partial<IdspWeekSummary>): IdspWeekSummary => ({
  year: 2026,
  week: 43 - offset,
  monday: todayIso(-7 * offset - 6),
  sunday: todayIso(-7 * offset),
  dueOn: todayIso(-7 * offset + 1),
  presumptiveCases: 0,
  laboratoryCases: 0,
  presumptiveReported: false,
  laboratoryReported: false,
  notReported: 0,
  overdue: false,
  ...partial,
});

const CHOLERA: IdspCaseRow = {
  id: id('c1'),
  diseaseCode: 'CHOLERA',
  disease: 'Cholera',
  immediate: true,
  source: 'Diagnosis',
  status: 'Open',
  patientId: id('p1'),
  patientName: 'Murugan Selvam',
  uhid: 'NH26000511',
  sex: 'Male',
  ageYears: 34,
  address: 'Kamaraj Nagar, Tiruchirappalli, Tamil Nadu',
  district: 'Tiruchirappalli',
  phone: '+91******4410',
  onsetDate: todayIso(-2),
  presumedAt: daysAgo(1),
  icdCode: 'A00.9',
  confirmedAt: null,
  testName: null,
  testResult: null,
  alertedAt: daysAgo(1),
  discardReason: null,
  version: 3,
};

const OVERVIEW: IdspOverview = {
  weeks: [
    week(0, { presumptiveCases: 3, laboratoryCases: 1 }),
    week(1, { presumptiveCases: 2, presumptiveReported: true, laboratoryCases: 1, overdue: true }),
  ],
  immediate: [CHOLERA],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/idsp/weeks': OVERVIEW,
};
