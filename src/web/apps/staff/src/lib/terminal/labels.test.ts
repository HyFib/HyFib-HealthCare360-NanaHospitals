import { describe, expect, it } from 'vitest';
import {
  isTerminalBusy,
  isTerminalSalePending,
  terminalDetailsLine,
  terminalSaleFits,
  terminalStatusLabel,
  unconfirmedGuidance,
} from './labels';

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

  it('does not treat an unconfirmed sale as pending or declined', () => {
    expect(isTerminalSalePending('Unconfirmed')).toBe(false);
    expect(terminalStatusLabel('Unconfirmed')).toBe('Not confirmed: the sale may still be on the terminal');
    expect(terminalStatusLabel('Lapsed')).toBe('Never confirmed: the outcome is unknown');
  });

  it('keeps the terminal busy until the end of the wait', () => {
    const sale = { status: 'Unconfirmed', retryAfter: '2026-11-14T05:07:00Z' };
    expect(isTerminalBusy(sale, new Date('2026-11-14T05:06:59Z'))).toBe(true);
    expect(isTerminalBusy(sale, new Date('2026-11-14T05:07:00Z'))).toBe(false);
    expect(isTerminalBusy({ status: 'Unconfirmed' }, new Date())).toBe(true);
    expect(isTerminalBusy({ status: 'Declined', retryAfter: '2999-01-01T00:00:00Z' }, new Date())).toBe(false);
  });

  it('says what to check and until when', () => {
    const text = unconfirmedGuidance({
      amount: 1250,
      createdAt: '2026-11-14T05:00:00Z',
      retryAfter: '2026-11-14T05:07:00Z',
    });
    expect(text).toContain('may still be on the terminal');
    expect(text).toContain("Check the terminal's screen");
    expect(text).toContain('record the payment from the slip');
    expect(text).toMatch(/sent at 10:30/);
    expect(text).toMatch(/wait until 10:37/);
  });
});
