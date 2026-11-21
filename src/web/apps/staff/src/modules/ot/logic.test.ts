import { ApiError, formatDateOnly, formatInr } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type {
  ConsentOnRecord,
  OtCalendarBooking,
  OtStoreItem,
  SafetyPhaseDto,
  SurgeryStatus,
  SurgicalConsentCheck,
  SurgicalConsentDto,
} from './api';
import {
  batchForLot,
  batchLabel,
  careChangeAfterSignIn,
  completeGate,
  consentGate,
  consentProblem,
  consentRecordedBy,
  consentSummary,
  countsGate,
  currentStepId,
  dayWindow,
  issuableQuantity,
  keepsAnaesthesiaConsent,
  localClashes,
  materialsLocked,
  missingConsents,
  moveEndsWaiver,
  onCaseTeam,
  otStoreProblem,
  overlaps,
  packPrice,
  phaseGate,
  placeBlocks,
  preOpGate,
  priceForBatch,
  receiveGate,
  saidNoConsent,
  saidNoNotice,
  siteChangeVoidsChecklist,
  siteNeedsMarking,
  slotConflicts,
  startGate,
  surgeonChangeEndsWaiver,
  surgerySteps,
  utilisationTotals,
  waiverGate,
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
    expect(phaseGate(booking({ safetyChecklist: phases(true), status: 'InProgress' }), 'SignOut').reason).toBe(
      'Complete the WHO Time-out first.',
    );
  });

  it('does the Sign-out only once surgery has started', () => {
    const beforeIncision = phaseGate(booking({ safetyChecklist: phases(true, true) }), 'SignOut');
    expect(beforeIncision.allowed).toBe(false);
    expect(beforeIncision.reason).toMatch(/after it has started/);
    expect(phaseGate(booking({ safetyChecklist: phases(true, true), status: 'InProgress' }), 'SignOut').allowed).toBe(
      true,
    );
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
    expect(startGate(booking()).reason).toBe(
      'Surgery cannot start until the WHO Sign-in and WHO Time-out are complete.',
    );
    expect(startGate(booking({ safetyChecklist: phases(true) })).reason).toBe(
      'Surgery cannot start until the WHO Time-out is complete.',
    );
    expect(startGate(booking({ safetyChecklist: phases(true, true) })).allowed).toBe(true);
    expect(startGate(booking({ safetyChecklist: phases(true, true), status: 'InProgress' })).reason).toBe(
      'Surgery has already started.',
    );
  });

  it('never allows completion before the Sign-out', () => {
    expect(completeGate(booking({ status: 'InProgress', safetyChecklist: phases(true, true) })).allowed).toBe(false);
    expect(completeGate(booking({ status: 'InProgress', safetyChecklist: phases(true, true, true) })).allowed).toBe(
      true,
    );
    expect(completeGate(booking({ safetyChecklist: phases(true, true, true) })).reason).toBe(
      'Only a surgery in progress can be completed.',
    );
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
    expect(currentStepId(booking({ status: 'Requested', scheduledStart: null, preOpComplete: false }))).toBe(
      'schedule',
    );
    expect(currentStepId(booking({ status: 'InPreOp', receivedInPreOpAt: T, safetyChecklist: phases(true) }))).toBe(
      'time-out',
    );
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
      booking({
        status: 'Completed',
        receivedInPreOpAt: T,
        startedAt: T,
        completedAt: T,
        safetyChecklist: phases(true, true, true),
      }),
    );
    expect(done.every((s) => s.state === 'done')).toBe(true);
    const cancelled = surgerySteps(booking({ status: 'Cancelled' }));
    expect(cancelled.filter((s) => s.state === 'stopped').map((s) => s.id)).toEqual([
      'schedule',
      'receive',
      'sign-in',
      'time-out',
      'start',
      'sign-out',
      'complete',
    ]);
  });

  it('counts WHO phases done', () => {
    expect(whoProgress({ signInDone: true, timeOutDone: true, signOutDone: false })).toBe(2);
  });
});

function calendarBooking(
  id: string,
  start: string,
  end: string,
  status: SurgeryStatus = 'Scheduled',
): OtCalendarBooking {
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
    expect(
      overlaps('2026-09-26T03:30:00Z', '2026-09-26T04:30:00Z', '2026-09-26T04:30:00Z', '2026-09-26T05:30:00Z'),
    ).toBe(false);
    expect(
      overlaps('2026-09-26T03:30:00Z', '2026-09-26T04:31:00Z', '2026-09-26T04:30:00Z', '2026-09-26T05:30:00Z'),
    ).toBe(true);
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
    expect(dayWindow([{ start: '2026-09-27T01:00:00Z', end: '2026-09-27T02:00:00Z' }], date)).toEqual({
      start: 480,
      end: 1200,
    });
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
    const [block] = placeBlocks([{ start: '2026-09-26T13:30:00Z', end: '2026-09-26T15:30:00Z' }], date, {
      start: 480,
      end: 1200,
    });
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
    expect(totals).toMatchObject({
      casesCompleted: 4,
      casesCancelled: 2,
      actualMinutes: 360,
      availableMinutes: 960,
      utilisationPercent: 37.5,
      firstCaseOnTimePercent: 50,
    });
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
    expect(
      otStoreProblem(new ApiError({ status: 422, title: 'Cannot complete this action', code: 'stock.no_store' })),
    ).toBe('no-store');
    expect(
      otStoreProblem(
        new ApiError({ status: 422, title: 'Cannot complete this action', code: 'stock.store_ambiguous' }),
      ),
    ).toBe('ambiguous');
    expect(
      otStoreProblem(new ApiError({ status: 0, title: 'Cannot reach the HealthCare360 server', code: 'network' })),
    ).toBeNull();
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

  it('fills a price-controlled implant at its NPPA ceiling when its pack says more', () => {
    expect(packPrice(60000, 57456)).toBe(57456);
    expect(packPrice(50000, 57456)).toBe(50000);
    expect(packPrice(1250, null)).toBe(1250);
    expect(packPrice(1250, undefined)).toBe(1250);
  });

  it('describes a batch in words', () => {
    expect(batchLabel(firstBatch, 'Nos')).toBe(
      `Lot LOT-A · expires ${formatDateOnly('2027-03-31')} · 2 Nos available · MRP ${formatInr(125000)}`,
    );
    expect(batchLabel({ ...firstBatch, expiryDate: null }, 'Nos')).toContain('no expiry date');
  });
});

describe('the theatre where a second person or a second look decides (maker-checker slice 10)', () => {
  const site = {
    procedureServiceItemId: null,
    procedureName: 'Inguinal hernia repair',
    laterality: 'Left' as const,
    siteLevel: null,
  };

  it('needs the site marked when a side or a level is booked, and not for a midline operation', () => {
    expect(siteNeedsMarking('Left', null)).toBe(true);
    expect(siteNeedsMarking('Bilateral', '')).toBe(true);
    expect(siteNeedsMarking('NotApplicable', 'L4–L5')).toBe(true);
    expect(siteNeedsMarking('NotApplicable', '  ')).toBe(false);
    expect(siteNeedsMarking(null, null)).toBe(false);
  });

  it('voids the checklist only for a change of side, level or procedure after the Sign-in', () => {
    const signedIn = { ...site, safetyChecklist: phases(true) };
    expect(siteChangeVoidsChecklist(signedIn, { ...site, laterality: 'Right' })).toBe(true);
    expect(siteChangeVoidsChecklist(signedIn, { ...site, siteLevel: 'T12' })).toBe(true);
    expect(siteChangeVoidsChecklist(signedIn, { ...site, procedureName: 'Femoral hernia repair' })).toBe(true);
    expect(siteChangeVoidsChecklist(signedIn, { ...site, procedureName: ' inguinal HERNIA repair ' })).toBe(false);
    expect(siteChangeVoidsChecklist({ ...site, safetyChecklist: phases() }, { ...site, laterality: 'Right' })).toBe(
      false,
    );
  });

  it('lets only the operating surgeon or the anaesthetist change the site after the Sign-in', () => {
    const team = { surgeonId: 'p-surgeon', anaesthetistId: 'p-anaesthetist' };
    expect(onCaseTeam(team, 'p-surgeon')).toBe(true);
    expect(onCaseTeam(team, 'p-anaesthetist')).toBe(true);
    expect(onCaseTeam(team, 'p-other')).toBe(false);
    expect(onCaseTeam({ surgeonId: null, anaesthetistId: null }, null)).toBe(false);
  });

  it('corrects implants instead of removing them once the operation has started', () => {
    expect(materialsLocked({ status: 'InPreOp', startedAt: null, safetyChecklist: phases(true, true) })).toBe(false);
    expect(materialsLocked({ status: 'InProgress', startedAt: T, safetyChecklist: phases(true, true) })).toBe(true);
    expect(materialsLocked({ status: 'Completed', startedAt: T, safetyChecklist: phases(true, true, true) })).toBe(
      true,
    );
  });

  it('records counts on the day of surgery until the Sign-out', () => {
    expect(countsGate({ status: 'InProgress', safetyChecklist: phases(true, true) }).allowed).toBe(true);
    expect(countsGate({ status: 'InProgress', safetyChecklist: phases(true, true, true) }).reason).toMatch(/Sign-out/);
    expect(countsGate({ status: 'Requested', safetyChecklist: phases() }).allowed).toBe(false);
  });
});

describe('consent before surgery and anaesthesia (mirrors SurgicalConsent)', () => {
  const procedure: SurgicalConsentCheck = {
    kind: 'Procedure',
    state: 'Recorded',
    consent: {
      id: 'c-1',
      kind: 'Procedure',
      granted: true,
      capturedAt: T,
      revokedAt: null,
      capturedByName: 'Priya R',
      signedFormAttached: true,
      reliedOnBy: null,
    },
  };
  const anaesthesia: SurgicalConsentCheck = {
    kind: 'Anaesthesia',
    state: 'Recorded',
    consent: {
      id: 'c-2',
      kind: 'Anaesthesia',
      granted: true,
      capturedAt: T,
      revokedAt: null,
      capturedByName: 'Priya R',
      signedFormAttached: false,
      reliedOnBy: null,
    },
  };
  const recorded: SurgicalConsentDto = {
    satisfied: true,
    recorded: true,
    since: '2026-09-25T03:00:00Z',
    checks: [procedure, anaesthesia],
    waiver: null,
    procedureConsentId: null,
    anaesthesiaConsentId: null,
    refused: false,
    anaesthesiaConsentRequiredAtSignIn: false,
    saidNo: false,
  };
  /** Nothing recorded under the patient's consents. */
  const missing: SurgicalConsentDto = {
    ...recorded,
    satisfied: false,
    recorded: false,
    checks: [
      { kind: 'Procedure', state: 'Missing', consent: null },
      { kind: 'Anaesthesia', state: 'Missing', consent: null },
    ],
  };
  const withdrawnCheck: SurgicalConsentCheck = {
    kind: 'Anaesthesia',
    state: 'Withdrawn',
    consent: {
      id: 'c-2',
      kind: 'Anaesthesia',
      granted: true,
      capturedAt: T,
      revokedAt: T,
      capturedByName: 'Priya R',
      signedFormAttached: false,
      reliedOnBy: null,
    },
  };
  /** Nothing recorded for the operation; the patient withdrew the consent to anaesthesia given since the booking. */
  const withdrawn: SurgicalConsentDto = {
    ...missing,
    saidNo: true,
    checks: [{ kind: 'Procedure', state: 'Missing', consent: null }, withdrawnCheck],
  };
  const waived: SurgicalConsentDto = {
    ...missing,
    satisfied: true,
    waiver: {
      at: T,
      surgeonUserId: 'u-surgeon',
      surgeonName: 'Dr. Arjun Rao',
      concurringUserId: 'u-second',
      concurringName: 'Dr. Kavita Shah',
      reason: 'Ruptured spleen, in shock; no relative reachable',
    },
  };
  const signedIn = (consent: SurgicalConsentDto | undefined) => ({
    ...booking({ safetyChecklist: phases(true, true) }),
    consent,
  });

  it('holds the incision until the patient’s consent is recorded, and says what is missing', () => {
    const gate = startGate(signedIn(missing));
    expect(gate.allowed).toBe(false);
    expect(gate.reason).toMatch(/consent to the operation \(high-risk procedure consent\) is not recorded/);
    expect(gate.reason).toMatch(/consent to anaesthesia is not recorded/);
    expect(startGate(signedIn(withdrawn)).reason).toMatch(/consent to anaesthesia was withdrawn/);
    expect(startGate(signedIn(recorded)).allowed).toBe(true);
    expect(startGate(signedIn(waived)).allowed).toBe(true);
  });

  it('holds the Sign-in and the Time-out, never the Sign-out', () => {
    const preOpDone = { ...booking(), consent: missing };
    expect(phaseGate(preOpDone, 'SignIn')).toMatchObject({ allowed: false, locked: false });
    expect(phaseGate({ ...preOpDone, safetyChecklist: phases(true) }, 'TimeOut').reason).toMatch(/consent/);
    expect(
      phaseGate({ ...preOpDone, safetyChecklist: phases(true, true), status: 'InProgress' }, 'SignOut').allowed,
    ).toBe(true);
    expect(phaseGate({ ...booking(), consent: recorded }, 'SignIn').allowed).toBe(true);
  });

  it('leaves a booking read without its consent to the server', () => {
    expect(consentGate({ consent: undefined }).allowed).toBe(true);
    expect(startGate(signedIn(undefined)).allowed).toBe(true);
  });

  it('says a waiver no longer stands once the booking is no longer an emergency', () => {
    expect(consentGate({ consent: { ...waived, satisfied: false } }).reason).toMatch(/no longer an emergency/);
  });

  it('offers the emergency path only to the operating surgeon of an emergency booking that has not started', () => {
    const emergency = {
      consent: missing,
      priority: 'Emergency' as const,
      status: 'InPreOp' as const,
      surgeonId: 'p-surgeon',
    };
    expect(waiverGate(emergency, 'p-surgeon').allowed).toBe(true);
    expect(waiverGate(emergency, 'p-anaesthetist').reason).toMatch(/Only the operating surgeon/);
    expect(waiverGate({ ...emergency, priority: 'Elective' }, 'p-surgeon').reason).toMatch(
      /Only an emergency operation/,
    );
    expect(waiverGate({ ...emergency, status: 'InProgress' }, 'p-surgeon').allowed).toBe(false);
    expect(waiverGate({ ...emergency, status: 'Postponed' }, 'p-surgeon').reason).toMatch(
      /neither closed nor postponed/,
    );
    expect(waiverGate({ ...emergency, consent: recorded }, 'p-surgeon').allowed).toBe(false);
    expect(waiverGate({ ...emergency, consent: waived }, 'p-surgeon').reason).toMatch(
      /already waived by Dr. Arjun Rao/,
    );
  });

  it('says what the incision relied on once the operation has started, never today’s consents', () => {
    expect(consentSummary({ consent: recorded, startedAt: null })).toBe('Recorded at the front office');
    expect(consentSummary({ consent: withdrawn, startedAt: null })).toBe(
      'Not recorded: consent to the operation (high-risk procedure consent) is not recorded; consent to anaesthesia was withdrawn',
    );
    expect(consentSummary({ consent: waived, startedAt: null })).toMatch(
      /^Waived in an emergency by Dr. Arjun Rao, Dr. Kavita Shah agreeing \(.+\): Ruptured spleen/,
    );
    // A waiver that no longer stands (the booking is no longer an emergency) is not shown as the consent.
    expect(consentSummary({ consent: { ...waived, satisfied: false }, startedAt: null })).toMatch(
      /^Not recorded: .+\. Consent was waived in an emergency, but the booking is no longer an emergency$/,
    );
    // The consent the incision relied on was withdrawn afterwards: the operation was still done with consent.
    const reliedOn = { ...missing, procedureConsentId: 'c-1', anaesthesiaConsentId: 'c-2' };
    expect(consentSummary({ consent: reliedOn, startedAt: T })).toBe(
      'Recorded at the front office before the incision',
    );
    expect(consentSummary({ consent: waived, startedAt: T })).toMatch(/^Waived in an emergency by Dr. Arjun Rao/);
    // The consent to the operation recorded, the consent to anaesthesia alone waived: the incision relied on the waiver too.
    expect(consentSummary({ consent: { ...waived, procedureConsentId: 'c-1' }, startedAt: T })).toMatch(
      /^Consent to the operation recorded at the front office before the incision; consent to anaesthesia waived in an emergency by Dr. Arjun Rao, Dr. Kavita Shah agreeing \(.+\): Ruptured spleen/,
    );
    // Under local anaesthesia no consent to anaesthesia was needed: the consent to the operation was all it relied on.
    const local: SurgicalConsentDto = {
      ...waived,
      procedureConsentId: 'c-1',
      checks: [procedure, { kind: 'Anaesthesia', state: 'NotNeeded', consent: null }],
    };
    expect(consentSummary({ consent: local, startedAt: T })).toBe('Recorded at the front office before the incision');
    // Started before the theatre read consents: nothing is on the booking either way, and nothing is claimed.
    expect(consentSummary({ consent: missing, startedAt: T })).toBeNull();
    expect(consentSummary({ consent: undefined, startedAt: null })).toBeNull();
  });

  it('words each missing consent as the server does', () => {
    expect(missingConsents(withdrawn).map(consentProblem)).toEqual([
      'consent to the operation (high-risk procedure consent) is not recorded',
      'consent to anaesthesia was withdrawn',
    ]);
    expect(consentProblem({ kind: 'Anaesthesia', state: 'BeforeBooking', consent: null })).toMatch(
      /recorded before this operation was booked/,
    );
    expect(consentProblem({ kind: 'Procedure', state: 'Refused', consent: null })).toBe(
      'the patient refused consent to the operation (high-risk procedure consent)',
    );
  });

  it('words a consent another operation relied on as one to take again for this operation', () => {
    const relied: SurgicalConsentCheck = {
      kind: 'Procedure',
      state: 'OtherOperation',
      consent: { ...recordedConsent('c-9', 'Procedure'), reliedOnBy: 'NHOT2600012' },
    };
    expect(consentProblem(relied)).toBe(
      'consent to the operation (high-risk procedure consent) was relied on by another operation (NHOT2600012); take it again for this operation',
    );
    // Another branch's operation is not named by its booking number (server `SurgicalConsent.AnotherBranch`).
    expect(
      consentProblem({ ...relied, consent: { ...recordedConsent('c-9', 'Procedure'), reliedOnBy: 'another branch' } }),
    ).toBe(
      'consent to the operation (high-risk procedure consent) was relied on by another operation (another branch); take it again for this operation',
    );
    expect(missingConsents({ ...recorded, satisfied: false, recorded: false, checks: [relied, anaesthesia] })).toEqual([
      relied,
    ]);
  });

  it('never offers the emergency path against the patient’s refusal, and says why', () => {
    const refusedCheck: SurgicalConsentCheck = {
      kind: 'Anaesthesia',
      state: 'Refused',
      consent: { ...recordedConsent('c-3', 'Anaesthesia'), granted: false },
    };
    const refused: SurgicalConsentDto = {
      ...missing,
      refused: true,
      saidNo: true,
      checks: [{ kind: 'Procedure', state: 'Missing', consent: null }, refusedCheck],
    };
    const emergency = {
      consent: refused,
      priority: 'Emergency' as const,
      status: 'InPreOp' as const,
      surgeonId: 'p-surgeon',
    };

    expect(saidNoConsent(refused)).toBe(refusedCheck);
    expect(saidNoConsent(missing)).toBeNull();
    expect(waiverGate(emergency, 'p-surgeon')).toEqual({
      allowed: false,
      reason:
        'The patient refused consent to anaesthesia: an emergency does not override a refusal. If the patient now agrees, record the new consent at the front office.',
    });
    expect(saidNoNotice(refusedCheck)).toMatch(/^The patient refused consent to anaesthesia/);
    expect(consentGate({ consent: refused }).reason).toMatch(/An emergency does not override the patient’s refusal/);
    expect(consentGate({ consent: refused }).reason).not.toMatch(/may go ahead without it/);

    // A refusal recorded after a waiver: the waiver no longer stands, and the screen says why.
    const waivedThenRefused: SurgicalConsentDto = { ...waived, ...refused, waiver: waived.waiver, satisfied: false };
    expect(consentSummary({ consent: waivedThenRefused, startedAt: null })).toMatch(
      /Consent was waived in an emergency, but the patient refused, and an emergency does not override a refusal$/,
    );
  });

  it('treats a withdrawal as the patient’s “no”, as a refusal: no waiver is offered, and one given before no longer stands', () => {
    const emergency = {
      consent: withdrawn,
      priority: 'Emergency' as const,
      status: 'InPreOp' as const,
      surgeonId: 'p-surgeon',
    };

    expect(saidNoConsent(withdrawn)).toBe(withdrawnCheck);
    expect(waiverGate(emergency, 'p-surgeon')).toEqual({
      allowed: false,
      reason:
        'The patient withdrew consent to anaesthesia: an emergency does not override a withdrawal. If the patient now agrees, record the new consent at the front office.',
    });
    const gate = consentGate({ consent: withdrawn });
    expect(gate.reason).toMatch(/consent to anaesthesia was withdrawn/);
    expect(gate.reason).toMatch(/An emergency does not override a consent the patient withdrew/);
    expect(gate.reason).not.toMatch(/may go ahead without it/);

    // A withdrawal recorded after a waiver: the server says the waiver no longer stands, and the screen says why.
    const waivedThenWithdrawn: SurgicalConsentDto = { ...withdrawn, waiver: waived.waiver, satisfied: false };
    expect(startGate(signedIn(waivedThenWithdrawn)).allowed).toBe(false);
    expect(consentSummary({ consent: waivedThenWithdrawn, startedAt: null })).toMatch(
      /Consent was waived in an emergency, but the patient withdrew consent, and an emergency does not override a withdrawal$/,
    );

    // A refusal and a withdrawal on record: the refusal is named first, as the server names it.
    const refusedProcedure: SurgicalConsentCheck = {
      kind: 'Procedure',
      state: 'Refused',
      consent: { ...recordedConsent('c-5', 'Procedure'), granted: false },
    };
    expect(saidNoConsent({ ...withdrawn, refused: true, checks: [withdrawnCheck, refusedProcedure] })).toBe(
      refusedProcedure,
    );
  });

  it('says who recorded each consent and whether the signed form is attached', () => {
    expect(consentRecordedBy(recordedConsent('c-1', 'Procedure'))).toBe('Recorded by Priya R; signed form attached');
    expect(
      consentRecordedBy({ ...recordedConsent('c-1', 'Procedure'), capturedByName: null, signedFormAttached: false }),
    ).toBe('Who recorded it is not known; no signed form attached');
  });

  it('asks the case team for a reason to change the anaesthesia or the priority after the Sign-in', () => {
    const signedIn = {
      anaesthesiaType: 'Spinal' as const,
      priority: 'Elective' as const,
      safetyChecklist: phases(true),
    };
    expect(careChangeAfterSignIn(signedIn, { anaesthesiaType: 'Local', priority: 'Elective' })).toBe(true);
    expect(careChangeAfterSignIn(signedIn, { anaesthesiaType: 'Spinal', priority: 'Emergency' })).toBe(true);
    expect(careChangeAfterSignIn(signedIn, { anaesthesiaType: 'Spinal', priority: 'Elective' })).toBe(false);
    expect(
      careChangeAfterSignIn(
        { ...signedIn, safetyChecklist: phases() },
        { anaesthesiaType: 'Local', priority: 'Emergency' },
      ),
    ).toBe(false);

    const heldAtSignIn = { consent: { ...recorded, anaesthesiaConsentRequiredAtSignIn: true } };
    expect(keepsAnaesthesiaConsent(heldAtSignIn, { anaesthesiaType: 'Local' })).toBe(true);
    expect(keepsAnaesthesiaConsent(heldAtSignIn, { anaesthesiaType: 'GA' })).toBe(false);
    expect(keepsAnaesthesiaConsent({ consent: recorded }, { anaesthesiaType: 'Topical' })).toBe(false);
  });

  it('warns that a move to another slot or another surgeon ends a consent waived in an emergency', () => {
    expect(moveEndsWaiver({ consent: waived, status: 'Scheduled' })).toBe(true);
    expect(moveEndsWaiver({ consent: waived, status: 'Requested' })).toBe(false);
    expect(moveEndsWaiver({ consent: recorded, status: 'Scheduled' })).toBe(false);

    expect(surgeonChangeEndsWaiver({ consent: waived, surgeonId: 'p-surgeon' }, 'p-other')).toBe(true);
    expect(surgeonChangeEndsWaiver({ consent: waived, surgeonId: 'p-surgeon' }, 'p-surgeon')).toBe(false);
    expect(surgeonChangeEndsWaiver({ consent: recorded, surgeonId: 'p-surgeon' }, 'p-other')).toBe(false);
  });
});

/** A consent the front office recorded for the patient, given, by Priya R with the signed form attached. */
function recordedConsent(id: string, kind: SurgicalConsentCheck['kind']): ConsentOnRecord {
  return {
    id,
    kind,
    granted: true,
    capturedAt: T,
    revokedAt: null,
    capturedByName: 'Priya R',
    signedFormAttached: true,
    reliedOnBy: null,
  };
}
