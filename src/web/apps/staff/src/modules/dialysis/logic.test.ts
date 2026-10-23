import { describe, expect, it } from 'vitest';
import type { DialysisBoard, DialysisSessionListItem, DialysisSessionStatus, DialysisShift } from './api';
import {
  checkDialyzer,
  checkMachine,
  effectiveMaxUses,
  isBookedByOther,
  isRescreeningDue,
  isSeroCompatible,
  nextUseLabel,
  sessionStage,
  suggestedUfMl,
  suggestMachine,
  weightAboveDry,
  weightLossMl,
  type MachineLike,
} from './logic';

const machine = (overrides: Partial<MachineLike & { id: string }> = {}) => ({
  id: 'm1',
  code: 'HD-01',
  seroDedication: 'Negative' as const,
  status: 'Available' as const,
  needsDisinfection: false,
  isActive: true,
  ...overrides,
});

describe('sero segregation (mirrors SeroSegregation)', () => {
  it('allows only a machine of exactly the patient category', () => {
    expect(isSeroCompatible('Negative', 'Negative')).toBe(true);
    expect(isSeroCompatible('HBsAg', 'Negative')).toBe(false);
    expect(isSeroCompatible('HCV', 'HBsAg')).toBe(false);
    expect(isSeroCompatible('HIV', 'HIV')).toBe(true);
  });

  it('disables an incompatible machine and says why, in the server wording', () => {
    const check = checkMachine(machine({ code: 'HD-04', seroDedication: 'HBsAg' }), 'Negative', 'assign');
    expect(check.eligible).toBe(false);
    expect(check.seroMatch).toBe(false);
    expect(check.blockers).toContain(
      'Machine HD-04 is dedicated to HBsAg-positive patients; this patient is sero-negative.',
    );
  });

  it('needs the patient to be enrolled with a sero status', () => {
    expect(checkMachine(machine(), null, 'schedule').blockers[0]).toMatch(/Enrol the patient/);
  });
});

describe('machine readiness (mirrors DialysisMachine.EnsureReadyFor)', () => {
  it('lets a machine that needs disinfection be booked, but not connected', () => {
    const dirty = machine({ needsDisinfection: true });
    const booking = checkMachine(dirty, 'Negative', 'assign');
    expect(booking.eligible).toBe(true);
    expect(booking.warnings).toContain('Needs disinfection before the patient is connected.');
    const connecting = checkMachine(dirty, 'Negative', 'start');
    expect(connecting.eligible).toBe(false);
    expect(connecting.blockers).toContain(
      'Record the disinfection of machine HD-01 before connecting the next patient.',
    );
  });

  it('refuses a machine that is running another session at the chair', () => {
    expect(checkMachine(machine({ status: 'InUse' }), 'Negative', 'start').eligible).toBe(false);
    expect(checkMachine(machine({ status: 'InUse' }), 'Negative', 'schedule').eligible).toBe(true);
  });

  it('refuses machines out of service or booked by someone else in the shift', () => {
    expect(checkMachine(machine({ status: 'Maintenance' }), 'Negative', 'assign').blockers[0]).toMatch(
      /out of service/,
    );
    expect(checkMachine(machine({ isActive: false }), 'Negative', 'assign').eligible).toBe(false);
    expect(checkMachine(machine(), 'Negative', 'assign', true).blockers[0]).toMatch(/booked for another patient/);
  });
});

function session(id: string, shift: DialysisShift, status: DialysisSessionStatus): DialysisSessionListItem {
  return {
    id,
    sessionNumber: id,
    patientId: `p-${id}`,
    uhid: 'U',
    patientName: 'P',
    scheduledDate: '2026-09-26',
    shift,
    status,
    machineId: 'm1',
    machineCode: 'HD-01',
    startedAt: null,
    endedAt: null,
    chargeStatus: 'NotDue',
    version: 1,
  };
}

describe('shift booking', () => {
  const board: Pick<DialysisBoard, 'machines'> = {
    machines: [
      {
        id: 'm1',
        code: 'HD-01',
        station: null,
        seroDedication: 'Negative',
        status: 'Available',
        needsDisinfection: false,
        sessions: [session('s1', 'Morning', 'Completed'), session('s2', 'Evening', 'Missed')],
      },
      {
        id: 'm2',
        code: 'HD-02',
        station: null,
        seroDedication: 'Negative',
        status: 'Available',
        needsDisinfection: true,
        sessions: [],
      },
      {
        id: 'm4',
        code: 'HD-04',
        station: null,
        seroDedication: 'HBsAg',
        status: 'Available',
        needsDisinfection: false,
        sessions: [],
      },
    ],
  };

  it('counts scheduled, running and completed sessions as holding the machine, but not missed ones', () => {
    expect(isBookedByOther(board, 'm1', 'Morning')).toBe(true);
    expect(isBookedByOther(board, 'm1', 'Morning', 's1')).toBe(false);
    expect(isBookedByOther(board, 'm1', 'Evening')).toBe(false);
    expect(isBookedByOther(board, 'm2', 'Morning')).toBe(false);
  });

  it('suggests a compatible free machine, preferring one that is already disinfected', () => {
    const machines = board.machines.map((m) => ({ ...m, isActive: true }));
    expect(suggestMachine(machines, 'Negative', 'Morning', board)?.code).toBe('HD-02');
    expect(suggestMachine(machines, 'Negative', 'Afternoon', board)?.code).toBe('HD-01');
    expect(suggestMachine(machines, 'HBsAg', 'Morning', board)?.code).toBe('HD-04');
    expect(suggestMachine(machines, 'HIV', 'Morning', board)).toBeNull();
  });
});

describe('dialyzer reuse (mirrors Dialyzer and DialysisPatientProfile)', () => {
  const dialyzer = { patientId: 'p1', serialNumber: 'DZ-1', isDiscarded: false, usesCount: 3, maxUses: 8 };

  it('forces single use for HBsAg-positive patients', () => {
    expect(effectiveMaxUses('Reuse', 'HBsAg', 8)).toBe(1);
    expect(effectiveMaxUses('Reuse', 'Negative', 8)).toBe(8);
    expect(effectiveMaxUses('SingleUse', 'Negative', 8)).toBe(1);
    expect(effectiveMaxUses('Reuse', 'HCV', 0)).toBe(1);
  });

  it('never uses another patient’s dialyzer, a discarded one, or one past its uses', () => {
    expect(checkDialyzer(dialyzer, 'p1')).toEqual({ usable: true, reason: null });
    expect(checkDialyzer(dialyzer, 'p2').reason).toMatch(/belongs to another patient/);
    expect(checkDialyzer({ ...dialyzer, isDiscarded: true }, 'p1').usable).toBe(false);
    expect(checkDialyzer({ ...dialyzer, usesCount: 8 }, 'p1').reason).toMatch(/maximum of 8/);
    expect(nextUseLabel(dialyzer)).toBe('Use 4 of 8');
  });
});

describe('fluid and weight', () => {
  it('suggests an ultrafiltration goal from the weight above dry weight', () => {
    expect(weightAboveDry(62.4, 60)).toBe(2.4);
    expect(suggestedUfMl(62.4, 60)).toBe(2400);
    expect(suggestedUfMl(59.5, 60)).toBe(0);
    expect(suggestedUfMl(null, 60)).toBeNull();
  });

  it('computes the weight lost like the server default for UF achieved', () => {
    expect(weightLossMl(62.4, 60.1)).toBe(2300);
    expect(weightLossMl(62.4, null)).toBeNull();
  });
});

describe('session stage and re-screening', () => {
  it('walks the session lifecycle', () => {
    expect(sessionStage({ status: 'Scheduled', machineId: null, preAssessedAt: null })).toBe('machine');
    expect(sessionStage({ status: 'Scheduled', machineId: 'm1', preAssessedAt: null })).toBe('pre-assessment');
    expect(sessionStage({ status: 'Scheduled', machineId: 'm1', preAssessedAt: '2026-09-26T03:00:00Z' })).toBe('start');
    expect(sessionStage({ status: 'InProgress', machineId: 'm1', preAssessedAt: 'x' })).toBe('running');
    expect(sessionStage({ status: 'Missed', machineId: null, preAssessedAt: null })).toBe('closed');
  });

  it('flags sero results older than 90 days', () => {
    expect(isRescreeningDue('2026-07-01', '2026-09-26')).toBe(false);
    expect(isRescreeningDue('2026-06-27', '2026-09-26')).toBe(true);
    expect(isRescreeningDue(null, '2026-09-26')).toBe(true);
  });
});
