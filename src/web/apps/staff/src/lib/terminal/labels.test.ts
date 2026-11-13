import { describe, expect, it } from 'vitest';
import { isTerminalSalePending, terminalDetailsLine, terminalSaleFits, terminalStatusLabel } from './labels';

describe('terminal sale labels', () => {
  it('writes the card details the terminal gave', () => {
    expect(terminalDetailsLine({ cardLast4: '1111', approvalCode: 'A1', rrn: '99' })).toBe(
      'Card ending 1111 · Approval A1 · RRN 99',
    );
    expect(terminalDetailsLine({ cardLast4: null, approvalCode: null, rrn: '99' })).toBe('RRN 99');
    expect(terminalDetailsLine({})).toBe('');
  });

  it('names who pays while the terminal waits', () => {
    expect(terminalStatusLabel('AwaitingCustomer')).toBe('Waiting for the patient to tap, insert or scan');
    expect(terminalStatusLabel('AwaitingCustomer', 'customer')).toBe('Waiting for the customer to tap, insert or scan');
    expect(terminalStatusLabel('Approved', 'customer')).toBe('Approved');
  });

  it('knows when the terminal is still waiting', () => {
    expect(isTerminalSalePending('Initiated')).toBe(true);
    expect(isTerminalSalePending('AwaitingCustomer')).toBe(true);
    expect(isTerminalSalePending('Approved')).toBe(false);
  });

  it('fits an earlier payment to the bill', () => {
    expect(terminalSaleFits({ amount: 100 }, 100, true)).toBe(true);
    expect(terminalSaleFits({ amount: 90 }, 100, true)).toBe(false);
    expect(terminalSaleFits({ amount: 90 }, 100, false)).toBe(true);
    expect(terminalSaleFits({ amount: 110 }, 100, false)).toBe(false);
  });
});
