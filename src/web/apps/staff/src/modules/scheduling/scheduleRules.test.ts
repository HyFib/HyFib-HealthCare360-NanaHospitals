import { describe, expect, it } from 'vitest';
import type { ScheduleDto } from './api';
import { findOverlaps, sessionProblem, sessionsOverlap, slotsPerSession, sortSessions, type SessionShape } from './scheduleRules';

describe('slotsPerSession', () => {
  it('counts whole slots only', () => {
    expect(slotsPerSession('09:00', '13:00', 15)).toBe(16);
    expect(slotsPerSession('09:00:00', '09:50:00', 15)).toBe(3);
    expect(slotsPerSession('13:00', '09:00', 15)).toBe(0);
    expect(slotsPerSession('09:00', '13:00', 0)).toBe(0);
  });
});

describe('sessionProblem', () => {
  const ok = { startTime: '09:00', endTime: '13:00', slotMinutes: 15, capacityPerSlot: 1 };

  it('accepts a normal session', () => {
    expect(sessionProblem(ok)).toBeNull();
  });

  it('reports the same field the server would', () => {
    expect(sessionProblem({ ...ok, slotMinutes: 3 })?.field).toBe('slotMinutes');
    expect(sessionProblem({ ...ok, endTime: '09:00' })?.field).toBe('endTime');
    expect(sessionProblem({ ...ok, endTime: '09:10' })?.message).toMatch(/shorter than one slot/);
    expect(sessionProblem({ ...ok, capacityPerSlot: 21 })?.field).toBe('capacityPerSlot');
    expect(sessionProblem({ ...ok, effectiveFrom: '2026-10-01', effectiveTo: '2026-09-30' })?.field).toBe('effectiveTo');
    expect(sessionProblem({ ...ok, startTime: '' })?.field).toBe('startTime');
  });
});

describe('sessionsOverlap', () => {
  const monday: SessionShape = {
    id: 's1',
    practitionerId: 'p1',
    weekday: 'Monday',
    startTime: '09:00:00',
    endTime: '13:00:00',
    effectiveFrom: '2026-01-01',
    effectiveTo: null,
    isActive: true,
  };

  it('detects clashing times on the same weekday', () => {
    expect(sessionsOverlap(monday, { ...monday, id: 's2', startTime: '12:00:00', endTime: '14:00:00' })).toBe(true);
  });

  it('allows back-to-back sessions, other days, other doctors and inactive rows', () => {
    expect(sessionsOverlap(monday, { ...monday, id: 's2', startTime: '13:00:00', endTime: '15:00:00' })).toBe(false);
    expect(sessionsOverlap(monday, { ...monday, id: 's2', weekday: 'Tuesday' })).toBe(false);
    expect(sessionsOverlap(monday, { ...monday, id: 's2', practitionerId: 'p2' })).toBe(false);
    expect(sessionsOverlap(monday, { ...monday, id: 's2', isActive: false })).toBe(false);
    expect(sessionsOverlap(monday, monday)).toBe(false);
  });

  it('ignores sessions whose effective dates never meet', () => {
    expect(sessionsOverlap({ ...monday, effectiveTo: '2026-06-30' }, { ...monday, id: 's2', effectiveFrom: '2026-07-01' })).toBe(false);
  });

  it('finds existing sessions a new multi-day session would clash with', () => {
    const existing = [
      { ...monday, id: 'x', weekday: 'Wednesday', practitionerName: 'Dr', departmentId: 'd', departmentName: null, facilityId: 'f', slotMinutes: 15, capacityPerSlot: 1, slotsPerSession: 16, mode: 'InPerson', room: null, notes: null, version: 1 },
    ] as ScheduleDto[];
    const clashes = findOverlaps(
      { practitionerId: 'p1', weekdays: ['Monday', 'Wednesday'], startTime: '10:00', endTime: '11:00', effectiveFrom: '2026-09-26', effectiveTo: null, isActive: true },
      existing,
    );
    expect(clashes.map((c) => c.id)).toEqual(['x']);
  });

  it('sorts sessions Monday to Sunday by start time', () => {
    const sorted = sortSessions([
      { weekday: 'Sunday' as const, startTime: '09:00:00' },
      { weekday: 'Monday' as const, startTime: '17:00:00' },
      { weekday: 'Monday' as const, startTime: '09:00:00' },
    ]);
    expect(sorted.map((s) => `${s.weekday} ${s.startTime}`)).toEqual(['Monday 09:00:00', 'Monday 17:00:00', 'Sunday 09:00:00']);
  });
});
