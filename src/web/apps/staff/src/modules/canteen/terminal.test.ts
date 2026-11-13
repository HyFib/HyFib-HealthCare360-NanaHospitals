import { describe, expect, it } from 'vitest';
import type { TerminalPaid } from '@/lib/terminal/types';
import { canTakeOnTerminal, paymentFields, terminalPaysBill } from './terminal';

const paid: TerminalPaid = { id: 'ts-1', mode: 'Upi', amount: 105, rrn: '123', approvalCode: null, cardLast4: null };

describe('canteen terminal payments', () => {
  it('offers the terminal for card and UPI bills with an amount', () => {
    expect(canTakeOnTerminal('Card', 105)).toBe(true);
    expect(canTakeOnTerminal('Upi', 105)).toBe(true);
    expect(canTakeOnTerminal('Card', 0)).toBe(false);
    expect(canTakeOnTerminal('Cash', 105)).toBe(false);
    expect(canTakeOnTerminal('StaffCredit', 105)).toBe(false);
  });

  it('needs the exact net amount', () => {
    expect(terminalPaysBill(paid, 105)).toBe(true);
    expect(terminalPaysBill(paid, 110)).toBe(false);
  });

  it('sends the terminal payment without a reference and in the mode used', () => {
    expect(paymentFields('Card', ' typed ', paid)).toEqual({
      paymentMode: 'Upi',
      paymentReference: null,
      terminalSaleId: 'ts-1',
    });
  });

  it('sends the typed reference for card and UPI only', () => {
    expect(paymentFields('Card', ' SLIP-1 ', null)).toEqual({
      paymentMode: 'Card',
      paymentReference: 'SLIP-1',
      terminalSaleId: null,
    });
    expect(paymentFields('Cash', 'SLIP-1', null).paymentReference).toBeNull();
    expect(paymentFields('Upi', '  ', null).paymentReference).toBeNull();
  });
});
