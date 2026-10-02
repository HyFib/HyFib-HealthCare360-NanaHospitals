import { describe, expect, it } from 'vitest';
import {
  appointmentAction,
  appointmentSummaryText,
  canConsult,
  dayOfStayLabel,
  isLate,
  parseWorklistView,
  roundAttention,
  roundSummaryText,
  sortRound,
  summariseAppointments,
  summariseRound,
} from './worklist';

type Facts = Parameters<typeof roundAttention>[0] & { name: string };

const patient = (name: string, facts: Partial<Facts> = {}): Facts => ({
  name,
  vitalsSeverity: null,
  hasCriticalResults: false,
  resultsToReview: 0,
  seenToday: true,
  status: 'Admitted',
  ...facts,
});

describe('worklist views', () => {
  it('reads only the known views from the address', () => {
    expect(parseWorklistView('rounds')).toBe('rounds');
    expect(parseWorklistView('appointments')).toBe('appointments');
    expect(parseWorklistView('beds')).toBeUndefined();
    expect(parseWorklistView(3)).toBeUndefined();
  });
});

describe('appointments', () => {
  const now = new Date('2026-10-02T05:00:00Z');

  it('counts a booking as late a quarter of an hour after its slot, until the patient arrives', () => {
    expect(isLate({ status: 'Booked', startsAt: '2026-10-02T04:44:00Z' }, now)).toBe(true);
    expect(isLate({ status: 'Confirmed', startsAt: '2026-10-02T04:46:00Z' }, now)).toBe(false);
    expect(isLate({ status: 'CheckedIn', startsAt: '2026-10-02T04:00:00Z' }, now)).toBe(false);
    expect(isLate({ status: 'NoShow', startsAt: '2026-10-02T04:00:00Z' }, now)).toBe(false);
  });

  it('starts a consultation only for a patient who has arrived and not been seen', () => {
    expect(canConsult({ status: 'CheckedIn', visitId: 'v1' })).toBe(true);
    expect(canConsult({ status: 'InConsultation', visitId: 'v1' })).toBe(true);
    expect(canConsult({ status: 'Booked', visitId: null })).toBe(false);
    expect(canConsult({ status: 'Completed', visitId: 'v1' })).toBe(false);
    expect(canConsult({ status: 'NoShow', visitId: null })).toBe(false);
  });

  it('offers one action per booking', () => {
    const can = { canWrite: true, isToday: true };
    const base = { visitId: 'v1', encounterId: null, isTelehealth: false } as const;

    expect(appointmentAction({ ...base, status: 'CheckedIn' }, can)).toBe('consult');
    expect(appointmentAction({ ...base, status: 'InConsultation', encounterId: 'e1' }, can)).toBe('consult');
    expect(appointmentAction({ ...base, status: 'Completed', encounterId: 'e1' }, can)).toBe('note');
    expect(appointmentAction({ ...base, status: 'Booked', visitId: null }, can)).toBeNull();
    expect(appointmentAction({ ...base, status: 'CheckedIn' }, { ...can, canWrite: false })).toBeNull();
    expect(appointmentAction({ ...base, status: 'Confirmed', isTelehealth: true }, can)).toBe('video');
    expect(appointmentAction({ ...base, status: 'CheckedIn', isTelehealth: true }, can)).toBe('video');
    expect(
      appointmentAction({ ...base, status: 'Confirmed', isTelehealth: true }, { ...can, isToday: false }),
    ).toBeNull();
    expect(appointmentAction({ ...base, status: 'NoShow', isTelehealth: true }, can)).toBeNull();
    // Video consultations switched off: no way into a call; a patient who came in is consulted like any other.
    const videoOff = { ...can, video: false };
    expect(appointmentAction({ ...base, status: 'Confirmed', isTelehealth: true, visitId: null }, videoOff)).toBeNull();
    expect(appointmentAction({ ...base, status: 'CheckedIn', isTelehealth: true }, videoOff)).toBe('consult');
  });

  it('summarises the day in words, leaving out what is zero', () => {
    const summary = summariseAppointments([
      { status: 'Booked' },
      { status: 'Confirmed' },
      { status: 'CheckedIn' },
      { status: 'InConsultation' },
      { status: 'Completed' },
      { status: 'NoShow' },
    ]);

    expect(summary).toEqual({ total: 6, toCome: 2, arrived: 2, seen: 1, noShow: 1 });
    expect(appointmentSummaryText(summary)).toBe('6 appointments: 2 to come, 2 arrived, 1 seen, 1 did not come');
    expect(appointmentSummaryText(summariseAppointments([{ status: 'Completed' }]))).toBe('1 appointment: 1 seen');
    expect(appointmentSummaryText(summariseAppointments([]))).toBe('No appointments');
  });
});

describe('ward round', () => {
  it('puts critical vital signs and critical results ahead of everything else', () => {
    expect(roundAttention(patient('critical vitals', { vitalsSeverity: 'Critical' }))).toBeGreaterThan(
      roundAttention(
        patient('abnormal, unseen, results', { vitalsSeverity: 'Abnormal', seenToday: false, resultsToReview: 3 }),
      ),
    );
    expect(
      roundAttention(patient('critical result', { hasCriticalResults: true, resultsToReview: 1 })),
    ).toBeGreaterThan(roundAttention(patient('abnormal vitals', { vitalsSeverity: 'Abnormal' })));
    expect(roundAttention(patient('nothing pending'))).toBe(0);
  });

  it('keeps the bed order, or brings the most urgent first and keeps bed order among equals', () => {
    const round = [
      patient('bed 1'),
      patient('bed 2', { seenToday: false }),
      patient('bed 3', { vitalsSeverity: 'Critical' }),
      patient('bed 4', { seenToday: false }),
    ];

    expect(sortRound(round, 'bed').map((p) => p.name)).toEqual(['bed 1', 'bed 2', 'bed 3', 'bed 4']);
    expect(sortRound(round, 'attention').map((p) => p.name)).toEqual(['bed 3', 'bed 2', 'bed 4', 'bed 1']);
    expect(round.map((p) => p.name)).toEqual(['bed 1', 'bed 2', 'bed 3', 'bed 4']);
  });

  it('summarises the round in words', () => {
    const summary = summariseRound([
      patient('a', { seenToday: false, vitalsSeverity: 'Critical' }),
      patient('b', { seenToday: false, resultsToReview: 2 }),
      patient('c', { hasCriticalResults: true, resultsToReview: 1 }),
    ]);

    expect(summary).toEqual({ total: 3, notSeen: 2, urgent: 2, withResults: 2 });
    expect(roundSummaryText(summary)).toBe(
      '3 in-patients: 2 not seen today, 2 need attention now, 2 with results to review',
    );
    expect(roundSummaryText(summariseRound([patient('d')]))).toBe('1 in-patient: all seen today');
    expect(roundSummaryText(summariseRound([]))).toBe('No in-patients');
  });

  it('names the first day of the stay', () => {
    expect(dayOfStayLabel(1)).toBe('Admitted today');
    expect(dayOfStayLabel(4)).toBe('Day 4');
  });
});
