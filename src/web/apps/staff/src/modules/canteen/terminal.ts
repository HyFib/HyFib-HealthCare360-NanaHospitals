import type { TerminalPaid } from '@/lib/terminal/types';
import type { CanteenPaymentMode } from './api';

/** "Take on terminal" is offered for card and UPI bills that have something to pay. */
export function canTakeOnTerminal(mode: CanteenPaymentMode, netAmount: number): boolean {
  return (mode === 'Card' || mode === 'Upi') && netAmount > 0;
}

/** A terminal payment pays a bill only when it is exactly the bill's net amount. */
export function terminalPaysBill(terminal: TerminalPaid, netAmount: number): boolean {
  return terminal.amount === netAmount;
}

/**
 * How the bill is paid on the wire. A terminal payment names its id and the mode the customer actually used (the server
 * takes the slip from the terminal, so no reference is typed); otherwise card and UPI send the typed reference.
 */
export function paymentFields(
  mode: CanteenPaymentMode,
  typedReference: string,
  terminal: TerminalPaid | null,
): { paymentMode: CanteenPaymentMode; paymentReference: string | null; terminalSaleId: string | null } {
  if (terminal) {
    return { paymentMode: terminal.mode, paymentReference: null, terminalSaleId: terminal.id };
  }
  return {
    paymentMode: mode,
    paymentReference: mode === 'Card' || mode === 'Upi' ? typedReference.trim() || null : null,
    terminalSaleId: null,
  };
}
