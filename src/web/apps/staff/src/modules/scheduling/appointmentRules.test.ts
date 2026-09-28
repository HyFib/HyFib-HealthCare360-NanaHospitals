import { describe, expect, it } from 'vitest';
import { appointmentActions, holdsSeat, isOpen, primaryAction, type ActionContext } from './appointmentRules';

const today = '2026-09-26';
const now = Date.parse('2026-09-26T11:00:00+05:30');
const desk: ActionContext = { today, now, canManage: true };

const morning = { status: 'Booked' as const, startsAt: '2026-09-26T10:30:00+05:30' };
const evening = { status: 'Confirmed' as const, startsAt: '2026-09-26T17:30:00+05:30' };
const tomorrow = { status: 'Booked' as const, startsAt: '2026-09-27T09:00:00+05:30' };

describe('appointment actions', () => {
  it('allows check-in only on the appointment day', () => {
    expect(appointmentActions(morning, desk).checkIn.allowed).toBe(true);
    const future = appointmentActions(tomorrow, desk).checkIn;
    expect(future.allowed).toBe(false);
    expect(future.reason).toMatch(/today/i);
  });

  it('allows a no-show only after the start time', () => {
    expect(appointmentActions(morning, desk).noShow.allowed).toBe(true);
    const early = appointmentActions(evening, desk).noShow;
    expect(early.allowed).toBe(false);
    expect(early.reason).toMatch(/after/);
  });

  it('confirms only booked appointments', () => {
    expect(appointmentActions(morning, desk).confirm.allowed).toBe(true);
    expect(appointmentActions(evening, desk).confirm).toEqual({ allowed: false, reason: 'Already confirmed.' });
  });

  it('closes every change once the patient has arrived or the booking ended', () => {
    for (const status of ['CheckedIn', 'InConsultation', 'Completed', 'Cancelled', 'NoShow', 'Rescheduled'] as const) {
      const actions = appointmentActions({ status, startsAt: morning.startsAt }, desk);
      expect(Object.values(actions).every((a) => !a.allowed && a.reason)).toBe(true);
    }
  });

  it('denies everything without the manage permission', () => {
    const actions = appointmentActions(morning, { ...desk, canManage: false });
    expect(Object.values(actions).every((a) => !a.allowed)).toBe(true);
  });

  it('suggests check-in today and confirmation for future bookings', () => {
    expect(primaryAction(morning, desk)).toBe('checkIn');
    expect(primaryAction(tomorrow, desk)).toBe('confirm');
    expect(primaryAction({ status: 'Confirmed', startsAt: tomorrow.startsAt }, desk)).toBeNull();
    expect(primaryAction({ status: 'Completed', startsAt: morning.startsAt }, desk)).toBeNull();
  });

  it('knows which statuses are open and which hold a seat', () => {
    expect(isOpen('Booked')).toBe(true);
    expect(isOpen('CheckedIn')).toBe(false);
    expect(holdsSeat('NoShow')).toBe(true);
    expect(holdsSeat('Rescheduled')).toBe(false);
    expect(holdsSeat('Cancelled')).toBe(false);
  });
});
