import { describe, expect, it } from 'vitest';
import {
  canCancel,
  canCheckIn,
  canMarkNoShow,
  checkupProblem,
  completionBlockers,
  countByStatus,
  nextStop,
  progress,
  type StopLike,
} from './itinerary';

const stops: StopLike[] = [
  { id: 'a', sequence: 2, status: 'Pending' },
  { id: 'b', sequence: 1, status: 'Done' },
  { id: 'c', sequence: 3, status: 'Skipped' },
  { id: 'd', sequence: 4, status: 'Pending' },
];

describe('itinerary', () => {
  it('measures progress', () => {
    expect(progress(stops)).toEqual({ done: 1, skipped: 1, pending: 2, total: 4, ratio: 0.5 });
    expect(progress([]).ratio).toBe(0);
  });

  it('finds the next pending stop in order', () => {
    expect(nextStop(stops)?.id).toBe('a');
    expect(nextStop(stops.map((s) => ({ ...s, status: 'Done' as const })))).toBeUndefined();
  });

  it('allows actions only in the right states', () => {
    expect(canCheckIn('Booked')).toBe(true);
    expect(canCheckIn('CheckedIn')).toBe(false);
    expect(canMarkNoShow('InProgress')).toBe(false);
    expect(canCancel('InProgress')).toBe(true);
    expect(canCancel('Completed')).toBe(false);
  });
});

describe('completionBlockers', () => {
  it('needs check-in, closed stops and the summary', () => {
    expect(completionBlockers({ status: 'Booked', summary: null, itinerary: stops })).toEqual([
      'Check the patient in first.',
    ]);
    expect(completionBlockers({ status: 'InProgress', summary: '', itinerary: stops })).toEqual([
      '2 stops are still pending. Complete or skip them.',
      "The reviewing doctor's summary is missing.",
    ]);
    expect(
      completionBlockers({
        status: 'InProgress',
        summary: 'All normal',
        itinerary: stops.map((s) => ({ ...s, status: 'Done' as const })),
      }),
    ).toEqual([]);
  });
});

describe('checkupProblem', () => {
  it('explains the package-billing refusals', () => {
    expect(
      checkupProblem(
        { code: 'package.not_billable', userMessage: 'Package HC-EXEC has no billable service item.' },
        'check-in',
      ),
    ).toEqual({
      title: 'This package cannot be billed yet',
      message: 'Package HC-EXEC has no billable service item.',
    });
    expect(checkupProblem({ code: 'booking.package_billed', userMessage: '' }, 'no-show').message).toContain(
      'credit note',
    );
    expect(checkupProblem({ code: 'billing.unavailable' }, 'check-in').title).toBe('Billing is not available');
  });

  it('explains a visit of another patient', () => {
    expect(
      checkupProblem(
        { status: 400, fieldErrors: { visitId: ['This visit does not belong to the patient.'] } },
        'check-in',
      ).title,
    ).toBe('That visit belongs to another patient');
  });

  it('falls back to the server message with an action title', () => {
    expect(checkupProblem({ code: 'something.else', userMessage: 'Server says no.' }, 'cancel')).toEqual({
      title: 'Could not cancel the checkup',
      message: 'Server says no.',
    });
  });
});

describe('countByStatus', () => {
  it('counts every status', () => {
    const counts = countByStatus([{ status: 'Booked' }, { status: 'Booked' }, { status: 'Completed' }]);
    expect(counts.Booked).toBe(2);
    expect(counts.Completed).toBe(1);
    expect(counts.NoShow).toBe(0);
  });
});
