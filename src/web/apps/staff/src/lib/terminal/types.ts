import type { PosTransactionStatus } from './labels';

export type TerminalMode = 'Card' | 'Upi';

/** A card/UPI payment taken on the hospital's terminal for a counter (pharmacy, canteen) as the server sends it. */
export interface CounterTerminalSale {
  id: string;
  counter: string;
  provider: string;
  terminalId: string;
  transactionId: string;
  amount: number;
  mode: TerminalMode;
  status: PosTransactionStatus;
  approvalCode: string | null;
  cardLast4: string | null;
  rrn: string | null;
  message: string | null;
  /** What it was recorded on (an invoice), or null while unused. */
  usedFor: string | null;
  createdAt: string;
  completedAt: string | null;
  /** For an unconfirmed sale: when a new sale may go to the terminal. Null for any other sale. */
  retryAfter?: string | null;
}

export interface StartTerminalSaleRequest {
  amount: number;
  terminalId: string;
  mode: TerminalMode;
  patientId?: string | null;
}

/** The four terminal endpoints of one counter; pharmacy and canteen each supply theirs. */
export interface TerminalSaleApi {
  /** Cache key prefix of this counter's terminal sales. */
  queryKey: readonly unknown[];
  start: (request: StartTerminalSaleRequest, options: { idempotencyKey: string }) => Promise<CounterTerminalSale>;
  get: (id: string, signal?: AbortSignal) => Promise<CounterTerminalSale>;
  /** Cancels on the terminal; for an unconfirmed sale `note` is the cashier's confirmation that the terminal shows no sale. */
  cancel: (id: string, note?: string) => Promise<CounterTerminalSale>;
  unused: (signal?: AbortSignal) => Promise<CounterTerminalSale[]>;
}

/** The slip details of a terminal payment attached to a counter screen. */
export interface TerminalPaid {
  id: string;
  mode: TerminalMode;
  amount: number;
  rrn: string | null;
  approvalCode: string | null;
  cardLast4: string | null;
}

export const toTerminalPaid = (sale: CounterTerminalSale): TerminalPaid => ({
  id: sale.id,
  mode: sale.mode,
  amount: sale.amount,
  rrn: sale.rrn,
  approvalCode: sale.approvalCode,
  cardLast4: sale.cardLast4,
});
