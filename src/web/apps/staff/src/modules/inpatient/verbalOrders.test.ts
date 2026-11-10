import { describe, expect, it } from 'vitest';
import type { MedicationOrderDto } from './nursingApi';
import { countersignState, writesVerbalOrder } from './verbalOrders';

// Who signs a ward order at once, and what waits on one for the doctor (the server decides the same and refuses otherwise).

const ME = '0192a000-0000-7000-8000-0000000000d1';
const COLLEAGUE = '0192a000-0000-7000-8000-0000000000d2';

describe('writesVerbalOrder', () => {
  it('signs a prescriber’s order in their own name at once', () => {
    expect(writesVerbalOrder(null, ME, true)).toBe(false);
    expect(writesVerbalOrder(ME, ME, true)).toBe(false);
  });

  it("makes a doctor's order in another doctor's name a verbal order", () => {
    expect(writesVerbalOrder(COLLEAGUE, ME, true)).toBe(true);
  });

  it('makes every order a nurse writes a verbal order, and a prescriber with no practitioner record’s too', () => {
    expect(writesVerbalOrder(COLLEAGUE, null, false)).toBe(true);
    expect(writesVerbalOrder(null, null, true)).toBe(true);
    expect(writesVerbalOrder(ME, ME, false)).toBe(true);
  });
});

describe('countersignState', () => {
  const order = (more: Partial<MedicationOrderDto>) =>
    ({
      awaitingCountersignature: false,
      countersignOverdue: false,
      stopPending: false,
      stopCountersignOverdue: false,
      ...more,
    }) as MedicationOrderDto;

  it('says nothing waits on a signed order', () => {
    expect(countersignState(order({}))).toBeNull();
  });

  it('says a verbal order awaits countersignature, and when it is past its time', () => {
    expect(countersignState(order({ awaitingCountersignature: true }))).toEqual({
      label: 'Awaiting countersignature',
      overdue: false,
    });
    expect(countersignState(order({ awaitingCountersignature: true, countersignOverdue: true }))).toEqual({
      label: 'Countersignature overdue',
      overdue: true,
    });
  });

  it('puts a nurse’s stop first: its doses are held', () => {
    expect(countersignState(order({ awaitingCountersignature: true, stopPending: true }))).toEqual({
      label: 'Stop pending: doses held',
      overdue: false,
    });
    expect(countersignState(order({ stopPending: true, stopCountersignOverdue: true }))?.overdue).toBe(true);
  });
});
