import { ApiError, formatDateOnly, formatInr } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type { OtCalendarBooking, OtStoreItem, SafetyPhaseDto, SurgeryStatus } from './api';
import {
  batchForLot,
  batchLabel,
  completeGate,
  currentStepId,
  dayWindow,
  issuableQuantity,
  localClashes,
  otStoreProblem,
  overlaps,
  phaseGate,
  placeBlocks,
  preOpGate,
  priceForBatch,
  receiveGate,
  slotConflicts,
  startGate,
  surgerySteps,
  utilisationTotals,
  whoProgress,
} from './logic';

const T = '2026-09-26T03:00:00Z';

function phases(signIn = false, timeOut = false, signOut = false): SafetyPhaseDto[] {
  return [
    { phase: 'SignIn', isComplete: signIn, completedAt: signIn ? T : null, items: [] },
    { phase: 'TimeOut', isComplete: timeOut, completedAt: timeOut ? T : null, items: [] },
    { phase: 'SignOut', isComplete: signOut, completedAt: signOut ? T : null, items: [] },
  ];
}

function booking(overrides: Partial<Parameters<typeof surgerySteps>[0]> = {}): Parameters<typeof surgerySteps>[0] {
  return {
    status: 'Scheduled',
    scheduledStart: '2026-09-26T03:30:00Z',
    preOpComplete: true,
    preOpChecklist: [],
    safetyChecklist: phases(),
    receivedInPreOpAt: null,
    startedAt: null,
    completedAt: null,
    ...overrides,
  };
}

describe('WHO checklist phase gating (mirrors SurgeryBooking.ConfirmSafety)', () => {
  it('opens the Sign-in on the day of surgery once the pre-op checklist is complete', () => {
    expect(phaseGate(booking(), 'SignIn')).toEqual({ allowed: true, locked: false, reason: null });
    expect(phaseGate(booking({ status: 'InPreOp' }), 'SignIn').allowed).toBe(true);
  });

  it('refuses the Sign-in until the pre-operative checklist is complete', () => {
    const gate = phaseGate(booking({ preOpComplete: false }), 'SignIn');
    expect(gate.allowed).toBe(false);
    expect(gate.reason).toMatch(/pre-operative checklist/);
  });

  it('keeps the phases in order', () => {
    expect(phaseGate(booking(), 'TimeOut').reason).toBe('Complete the WHO Sign-in first.');
    expect(phaseGate(booking({ safetyChecklist: phases(true) }), 'TimeOut').allowed).toBe(true);
    expect(phaseGate(booking({ safetyChecklist: phases(true), status: 'InProgress' }), 'SignOut').reason).toBe('Complete the WHO Time-out first.');
  });

  it('does the Sign-out only once surgery has started', () => {
    const beforeIncision = phaseGate(booking({ safetyChecklist: phases(true, true) }), 'SignOut');
    expect(beforeIncision.allowed).toBe(false);
    expect(beforeIncision.reason).toMatch(/after it has started/);
    expect(phaseGate(booking({ safetyChecklist: phases(true, true), status: 'InProgress' }), 'SignOut').allowed).toBe(true);
  });

  it('locks a completed phase', () => {
    const gate = phaseGate(booking({ safetyChecklist: phases(true) }), 'SignIn');
    expect(gate).toMatchObject({ allowed: false, locked: true });
  });

  it('explains why a requested, postponed or cancelled case cannot be checked in', () => {
    expect(phaseGate(booking({ status: 'Requested' }), 'SignIn').reason).toMatch(/Schedule the surgery first/);
    expect(phaseGate(booking({ status: 'Postponed' }), 'SignIn').reason).toMatch(/Schedule the surgery first/);
    expect(phaseGate(booking({ status: 'Cancelled' }), 'SignIn').reason).toBe('This booking is cancelled.');
  });
});

describe('surgery start and completion gates', () => {
  it('never allows the incision before the Sign-in and Time-out', () => {
    expect(startGate(booking()).reason).toBe('Surgery cannot start until the WHO Sign-in and WHO Time-out are complete.');
    expect(startGate(booking({ safetyChecklist: phases(true) })).reason).toBe('Surgery cannot start until the WHO Time-out is complete.');
    expect(startGate(booking({ safetyChecklist: phases(true, true) })).allowed).toBe(true);
    expect(startGate(booking({ safetyChecklist: phases(true, true), status: 'InProgress' })).reason).toBe('Surgery has already started.');
  });

  it('never allows completion before the Sign-out', () => {
    expect(completeGate(booking({ status: 'InProgress', safetyChecklist: phases(true, true) })).allowed).toBe(false);
    expect(completeGate(booking({ status: 'InProgress', safetyChecklist: phases(true, true, true) })).allowed).toBe(true);
    expect(completeGate(booking({ safetyChecklist: phases(true, true, true) })).reason).toBe('Only a surgery in progress can be completed.');
  });

  it('locks the pre-op checklist after the Sign-in', () => {
    expect(preOpGate(booking()).allowed).toBe(true);
    expect(preOpGate(booking({ safetyChecklist: phases(true) })).allowed).toBe(false);
    expect(preOpGate(booking({ status: 'Completed', safetyChecklist: phases(true, true, true) })).allowed).toBe(false);
  });

  it('receives only scheduled patients', () => {
    expect(receiveGate({ status: 'Scheduled' }).allowed).toBe(true);
    expect(receiveGate({ status: 'InPreOp' }).reason).toMatch(/already been received/);
    expect(receiveGate({ status: 'Requested' }).reason).toMatch(/Schedule/);
  });
});

describe('day-of-surgery steps', () => {
  it('points at the next thing to do', () => {
    expect(currentStepId(booking())).toBe('receive');
    expect(currentStepId(booking({ status: 'Requested', scheduledStart: null, preOpComplete: false }))).toBe('schedule');
    expect(currentStepId(booking({ status: 'InPreOp', receivedInPreOpAt: T, safetyChecklist: phases(true) }))).toBe('time-out');
  });

  it('shows the optional pre-op reception as skipped when the team went straight to the Sign-in', () => {
    const steps = surgerySteps(booking({ safetyChecklist: phases(true) }));
    expect(steps.find((s) => s.id === 'receive')?.state).toBe('skipped');
    expect(steps.find((s) => s.id === 'time-out')?.state).toBe('current');
  });

  it('does not treat a postponed booking as skipping the schedule step', () => {
    const steps = surgerySteps(booking({ status: 'Postponed' }));
    expect(steps.find((s) => s.id === 'schedule')?.state).toBe('current');
    expect(steps.find((s) => s.id === 'pre-op')?.state).toBe('done');
  });

  it('marks everything done after completion and stops a cancelled case', () => {
    const done = surgerySteps(
      booking({ status: 'Completed', receivedInPreOpAt: T, startedAt: T, completedAt: T, safetyChecklist: phases(true, true, true) }),
    );
    expect(done.every((s) => s.state === 'done')).toBe(true);
    const cancelled = surgerySteps(booking({ status: 'Cancelled' }));
    expect(cancelled.filter((s) => s.state === 'stopped').map((s) => s.id)).toEqual(['schedule', 'receive', 'sign-in', 'time-out', 'start', 'sign-out', 'complete']);
  });

  it('counts WHO phases done', () => {
    expect(whoProgress({ signInDone: true, timeOutDone: true, signOutDone: false })).toBe(2);
  });
});

function calendarBooking(id: string, start: string, end: string, status: SurgeryStatus = 'Scheduled'): OtCalendarBooking {
  return {
    id,
    bookingNumber: id,
    patientId: 'p',
    uhid: 'U1',
    patientName: 'Test',
    gender: 'Male',
    age: '40Y',
    procedureName: 'Appendicectomy',
    laterality: 'NotApplicable',
    priority: 'Elective',
    status,
    theatreId: 't1',
    start,
    end,
    surgeonId: 's',
    surgeonName: 'Dr S',
    anaesthetistName: null,
    anaesthesiaType: 'GA',
    signInDone: false,
    timeOutDone: false,
    signOutDone: false,
  };
}

describe('double-booking hints (half-open intervals like the server)', () => {
  it('allows back-to-back cases and flags overlaps', () => {
    expect(overlaps('2026-09-26T03:30:00Z', '2026-09-26T04:30:00Z', '2026-09-26T04:30:00Z', '2026-09-26T05:30:00Z')).toBe(false);
    expect(overlaps('2026-09-26T03:30:00Z', '2026-09-26T04:31:00Z', '2026-09-26T04:30:00Z', '2026-09-26T05:30:00Z')).toBe(true);
  });

  it('ignores finished or cancelled cases and the booking being moved', () => {
    const board = [
      calendarBooking('a', '2026-09-26T03:30:00Z', '2026-09-26T05:30:00Z'),
      calendarBooking('b', '2026-09-26T03:30:00Z', '2026-09-26T05:30:00Z', 'Completed'),
      calendarBooking('c', '2026-09-26T04:00:00Z', '2026-09-26T05:00:00Z', 'InProgress'),
    ];
    const slot = { start: '2026-09-26T04:00:00Z', end: '2026-09-26T04:30:00Z' };
    expect(localClashes(slot, board).map((b) => b.id)).toEqual(['a', 'c']);
    expect(localClashes({ ...slot, bookingId: 'a' }, board).map((b) => b.id)).toEqual(['c']);
  });

  it('reads the clashes from a 409 slot conflict only', () => {
    const conflict = new ApiError({
      status: 409,
      title: 'Conflict',
      code: 'surgery.slot_conflict',
      data: [{ bookingId: 'x', kind: 'Surgeon', start: '2026-09-26T03:30:00Z', end: '2026-09-26T05:30:00Z' }],
    });
    expect(slotConflicts(conflict)?.[0]?.kind).toBe('Surgeon');
    expect(slotConflicts(new ApiError({ status: 409, title: 'Conflict', code: 'concurrency' }))).toBeNull();
    expect(slotConflicts(new Error('x'))).toBeNull();
  });
});

describe('day board layout', () => {
  const date = '2026-09-26';

  it('shows 8 am to 8 pm and widens to whole hours around early or late cases', () => {
    expect(dayWindow([], date)).toEqual({ start: 480, end: 1200 });
    // 06:30–07:30 IST and 21:00–22:10 IST
    const early = { start: '2026-09-26T01:00:00Z', end: '2026-09-26T02:00:00Z' };
    const late = { start: '2026-09-26T15:30:00Z', end: '2026-09-26T16:40:00Z' };
    expect(dayWindow([early, late], date)).toEqual({ start: 360, end: 1380 });
    // A case on another day does not stretch the window.
    expect(dayWindow([{ start: '2026-09-27T01:00:00Z', end: '2026-09-27T02:00:00Z' }], date)).toEqual({ start: 480, end: 1200 });
  });

  it('places overlapping cases side by side and back-to-back cases in one lane', () => {
    const window = { start: 480, end: 1200 };
    const placed = placeBlocks(
      [
        { id: 'a', start: '2026-09-26T03:30:00Z', end: '2026-09-26T05:30:00Z' }, // 09:00–11:00
        { id: 'b', start: '2026-09-26T05:00:00Z', end: '2026-09-26T06:00:00Z' }, // 10:30–11:30 (overrun clash)
        { id: 'c', start: '2026-09-26T06:00:00Z', end: '2026-09-26T07:00:00Z' }, // 11:30–12:30 (back to back)
      ],
      date,
      window,
    );
    const byId = Object.fromEntries(placed.map((p) => [p.item.id, p]));
    expect(byId.a).toMatchObject({ startMinute: 540, endMinute: 660, lane: 0, lanes: 2 });
    expect(byId.b).toMatchObject({ lane: 1, lanes: 2 });
    expect(byId.c).toMatchObject({ lane: 0, lanes: 1 });
  });

  it('clips cases that run past the window', () => {
    const [block] = placeBlocks([{ start: '2026-09-26T13:30:00Z', end: '2026-09-26T15:30:00Z' }], date, { start: 480, end: 1200 });
    expect(block).toMatchObject({ startMinute: 1140, endMinute: 1200, clippedEnd: true, clippedStart: false });
  });
});

describe('utilisation totals', () => {
  it('adds theatres and recomputes the percentages', () => {
    const metrics = (actual: number, available: number, first: number, onTime: number) => ({
      casesCompleted: 2,
      casesCancelled: 1,
      casesPostponed: 0,
      bookedMinutes: actual,
      actualMinutes: actual,
      availableMinutes: available,
      firstCases: first,
      firstCasesOnTime: onTime,
    });
    const totals = utilisationTotals([{ metrics: metrics(240, 480, 1, 1) }, { metrics: metrics(120, 480, 1, 0) }]);
    expect(totals).toMatchObject({ casesCompleted: 4, casesCancelled: 2, actualMinutes: 360, availableMinutes: 960, utilisationPercent: 37.5, firstCaseOnTimePercent: 50 });
    expect(utilisationTotals([]).utilisationPercent).toBeNull();
  });
});

describe('OT store materials', () => {
  const item: OtStoreItem = {
    itemId: 'i1',
    code: 'IMP-001',
    name: 'Knee prosthesis, size 4',
    genericName: null,
    manufacturer: 'OrthoCo',
    type: 'Implant',
    baseUnit: 'Nos',
    available: 7,
    nearestExpiry: '2027-03-31',
    mrp: 125000,
    batches: [
      { batchId: 'b1', batchNumber: 'LOT-A', expiryDate: '2027-03-31', available: 2, mrp: 125000 },
      { batchId: 'b2', batchNumber: 'LOT-B', expiryDate: '2027-09-30', available: 5, mrp: 130000 },
    ],
  };
  const firstBatch = item.batches[0]!;

  it('reads a missing or ambiguous OT store from the 422 codes only', () => {
    expect(otStoreProblem(new ApiError({ status: 422, title: 'Cannot complete this action', code: 'stock.no_store' }))).toBe('no-store');
    expect(otStoreProblem(new ApiError({ status: 422, title: 'Cannot complete this action', code: 'stock.store_ambiguous' }))).toBe('ambiguous');
    expect(otStoreProblem(new ApiError({ status: 0, title: 'Cannot reach the HealthCare360 server', code: 'network' }))).toBeNull();
    expect(otStoreProblem(null)).toBeNull();
  });

  it('finds the chosen batch by its lot number', () => {
    expect(batchForLot(item, ' lot-b ')?.batchId).toBe('b2');
    expect(batchForLot(item, 'LOT-Z')).toBeNull();
    expect(batchForLot(item, '')).toBeNull();
    expect(batchForLot(null, 'LOT-A')).toBeNull();
  });

  it('limits the issue to the chosen lot, otherwise to all the stock', () => {
    expect(issuableQuantity(item, 'LOT-A')).toBe(2);
    expect(issuableQuantity(item, '')).toBe(7);
    expect(issuableQuantity(item, 'LOT-Z')).toBe(7);
  });

  it('follows the batch MRP unless the user typed a price', () => {
    expect(priceForBatch(null, null, 125000)).toBe(125000);
    expect(priceForBatch(125000, 125000, 130000)).toBe(130000);
    expect(priceForBatch(99000, 125000, 130000)).toBe(99000);
  });

  it('describes a batch in words', () => {
    expect(batchLabel(firstBatch, 'Nos')).toBe(`Lot LOT-A · expires ${formatDateOnly('2027-03-31')} · 2 Nos available · MRP ${formatInr(125000)}`);
    expect(batchLabel({ ...firstBatch, expiryDate: null }, 'Nos')).toContain('no expiry date');
  });
});
