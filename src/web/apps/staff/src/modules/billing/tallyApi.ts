import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { TallyExportStatus } from './labels';

/**
 * Tally day book: /api/v1/billing/tally (INT-TALLY). Permission `billing.tally.export` for the day book, posting,
 * the file and the checks; `billing.tally.configure` to change the ledger names.
 */

/** Outcome of a reconciliation check (mirrors `TallyCheckStatus`). */
export type TallyCheckStatus = 'NotChecked' | 'Matched' | 'Differences' | 'Failed';

/** Where a voucher stands (mirrors `TallyVoucherStatus`). */
export type TallyVoucherStatus =
  | 'NotSent'
  | 'InTally'
  | 'Refused'
  | 'Unconfirmed'
  | 'ChangedSinceSent'
  | 'DiffersInTally'
  | 'NoLongerInHc360'
  | 'OnlyInTally'
  | 'BillOfAnotherDay';

/** What Tally last answered for a voucher (mirrors `TallyVoucherAnswer`). */
export type TallyVoucherAnswer =
  'None' | 'Created' | 'Altered' | 'Accepted' | 'Rejected' | 'Unconfirmed' | 'NotSent' | 'Imported';

/** How the voucher compared with Tally's copy at the last check (mirrors `TallyMatch`). */
export type TallyMatch =
  'NotChecked' | 'Matched' | 'Missing' | 'AmountDiffers' | 'Changed' | 'NotInBooks' | 'OnlyInTally';

/** One day's posting record (`TallyExportDto`). */
export interface TallyExportDto {
  id: string;
  date: string;
  status: TallyExportStatus;
  voucherCount: number;
  attempts: number;
  createdCount: number;
  alteredCount: number;
  errorCount: number;
  createdAt: string;
  lastAttemptAt: string | null;
  postedAt: string | null;
  message: string | null;
  /** Vouchers of the current day book Tally holds as they are now (confirmed, or imported from the file). */
  confirmedCount: number;
  rejectedCount: number;
  fileDownloadedAt: string | null;
  importedAt: string | null;
  importedByName: string | null;
  recordsCheck: TallyCheckStatus;
  recordsCheckedAt: string | null;
  recordsDifferences: number;
  tallyCheck: TallyCheckStatus;
  tallyCheckedAt: string | null;
  tallyDifferences: number;
  tallyCheckMessage: string | null;
}

/** How a part of a line on a party's ledger stands against the party's bills (mirrors `AccountingBillType`). */
export type AccountingBillType = 'NewRef' | 'AgainstRef' | 'Advance' | 'OnAccount';

/** How money moved through the bank (mirrors `AccountingBankTransaction`). */
export type AccountingBankTransaction = 'Others' | 'Cheque' | 'EFundTransfer';

export interface AccountingBillAllocation {
  name: string | null;
  type: AccountingBillType;
  amount: number;
}

export interface AccountingBankAllocation {
  date: string;
  transactionType: AccountingBankTransaction;
  amount: number;
  instrumentNumber: string | null;
  instrumentDate: string | null;
  bankName: string | null;
}

export interface AccountingEntry {
  ledgerName: string;
  amount: number;
  isDebit: boolean;
  costCentre: string | null;
  /** Bill references on a payer's or supplier's ledger (New Ref, Agst Ref, On Account). */
  billAllocations?: AccountingBillAllocation[];
  /** The cheque or UTR on a line of a bank ledger. */
  bankAllocations?: AccountingBankAllocation[];
}

/** The GST party of a B2B sale or a purchase. */
export interface AccountingParty {
  ledgerName: string;
  gstin: string | null;
  stateName: string | null;
  placeOfSupply: string | null;
}

export interface AccountingVoucher {
  voucherType: string;
  date: string;
  number: string;
  narration: string;
  entries: AccountingEntry[];
  reference: string | null;
  remoteId: string | null;
  key: string;
  amount: number;
  party?: AccountingParty | null;
  /** A purchase's bill as the supplier numbered and dated it. */
  supplierInvoiceNumber?: string | null;
  supplierInvoiceDate?: string | null;
}

/** Counts of a day's vouchers by where they stand (`TallySending`). */
export interface TallySending {
  vouchers: number;
  inTally: number;
  notSent: number;
  refused: number;
  changedSinceSent: number;
  differsInTally: number;
  noLongerInHc360: number;
  onlyInTally: number;
  complete: boolean;
}

export interface TallyVoucherDto {
  key: string;
  remoteId: string | null;
  voucherType: string;
  number: string;
  amount: number;
  status: TallyVoucherStatus;
  answer: TallyVoucherAnswer;
  answerMessage: string | null;
  answeredAt: string | null;
  match: TallyMatch;
  tallyAmount: number | null;
  tallyNumber: string | null;
  tallyDetail: string | null;
  checkedAt: string | null;
  /** For a bill found only in Tally on this day: the day HC360 has it on, to be posted again. */
  billDate?: string | null;
}

export interface TallyDayVouchers {
  date: string;
  /** Null when the day has not been posted. */
  status: TallyExportStatus | null;
  sending: TallySending;
  vouchers: TallyVoucherDto[];
}

/** One figure of the day as the records have it and as the day book has it (`TallyCheckLine`). */
export interface TallyCheckLine {
  group: string;
  item: string;
  records: number;
  dayBook: number;
  recordsCount: number | null;
  dayBookCount: number | null;
  note: string | null;
  /** Totals only: HC360 has no second record of the figure to compare it with. */
  informational: boolean;
  difference: number;
  matches: boolean;
}

export interface TallyDayCheck {
  date: string;
  status: TallyExportStatus | null;
  records: TallyCheckStatus;
  lines: TallyCheckLine[];
  notes: string[];
  sending: TallySending;
  tally: TallyCheckStatus;
  tallyCheckedAt: string | null;
  tallyDifferences: number;
  tallyMessage: string | null;
  /** Tally is set up and the day has been posted, so it can be read back. */
  canCheckTally: boolean;
}

/** What a patient's voucher's narration says of the patient (mirrors `TallyNarrationPatient`). */
export type TallyNarrationPatient = 'None' | 'UhidOnly' | 'NameAndUhid';

/** Which stores' goods receipts the day book posts as purchases (mirrors `TallyPurchaseStores`). */
export type TallyPurchaseStores = 'Pharmacy' | 'All' | 'None';

/** The voucher types the day book's sales and purchases go to (`TallyVoucherTypes`). */
export interface TallyVoucherTypes {
  inPatient: string;
  outPatient: string;
  laboratory: string;
  pharmacy: string;
  purchase: string;
}

/** A counter that bills its own sales (pharmacy, canteen) in the ledger settings. */
export interface TallyCounterLedgers {
  include: boolean;
  sales: string | null;
  onAccount: string | null;
  modeLedgers: Record<string, string>;
}

/** The ledger names the day book posts to (setting `billing.tally.ledgers`, `TallyLedgerMap`). */
export interface TallyLedgerMap {
  defaultIncome: string;
  incomeByDepartment: Record<string, string>;
  discount: string;
  outputCgst: string;
  outputSgst: string;
  /** GST on supplies to another state (a payer or buyer registered there). */
  outputIgst: string;
  roundOff: string;
  patientDebtors: string;
  patientAdvances: string;
  payerReceivables: string;
  ledgerPerPayer: boolean;
  modeLedgers: Record<string, string>;
  creditNotes: string;
  writeOffs: string;
  insuranceDisallowances: string;
  doctorFeeExpense: string;
  doctorPayablePrefix: string;
  tdsPayable: string;
  doctorRecoveries: string;
  payoutBank: string;
  /** The settlement journal (gateway reconciliation): the bank paid, the gateway's charges and the GST on them. */
  gatewaySettlementBank: string;
  gatewayCharges: string;
  gatewayInputGst: string;
  /** The card and UPI settlement journal: the bank the acquirer pays into and its charges (GST on them: gatewayInputGst). */
  posSettlementBank: string;
  posCharges: string;
  /** Supplier documents: goods returned, input GST taken back, payable adjustments, and each supplier's ledger. */
  purchaseReturns: string;
  inputCgst: string;
  inputSgst: string;
  inputIgst: string;
  vendorAdjustments: string;
  ledgerPerVendor: boolean;
  sundryCreditors: string;
  /** Goods bought (a goods receipt's taxable value), per GST rate with `salesLedgerPerGstRate`. */
  purchases: string;
  purchasesFrom: TallyPurchaseStores;
  pharmacy: TallyCounterLedgers;
  canteen: TallyCounterLedgers;
  /** Sales of the pharmacy and the canteen, and purchases, go to a ledger per GST rate ("Pharmacy Sales @ 12%"). */
  salesLedgerPerGstRate: boolean;
  /** GST goes to a ledger per tax and rate ("Output CGST @ 6%" for 12%). */
  gstLedgerPerRate: boolean;
  /** The pharmacy posts one voucher per invoice; off, its day's summary. */
  pharmacyPerInvoice: boolean;
  voucherTypes: TallyVoucherTypes;
  patientInNarration: TallyNarrationPatient;
  /** The ledgers that are bank accounts in Tally; null: the ledgers HC360 posts bank money to. */
  bankLedgers: string[] | null;
  /**
   * The version of the facility's saved names; absent while the facility uses the hospital-wide ones. Sent back with a
   * change, so names saved meanwhile by someone else are not overwritten (REL-04).
   */
  version?: number;
}

const BASE = '/api/v1/billing/tally';

export const tallyKeys = {
  all: ['billing', 'tally'] as const,
  day: (date: string) => ['billing', 'tally', 'day', date] as const,
  ledgers: ['billing', 'tally', 'ledgers'] as const,
};

export const tallyQueries = {
  exports: (from: string, to: string) =>
    queryOptions({
      queryKey: ['billing', 'tally', 'exports', from, to],
      queryFn: ({ signal }) => api.get<TallyExportDto[]>(`${BASE}/exports`, { query: { from, to }, signal }),
      refetchInterval: 30_000,
    }),
  dayBook: (date: string) =>
    queryOptions({
      queryKey: ['billing', 'tally', 'day-book', date],
      queryFn: ({ signal }) => api.get<AccountingVoucher[]>(`${BASE}/day-book`, { query: { date }, signal }),
    }),
  dayVouchers: (date: string) =>
    queryOptions({
      queryKey: [...tallyKeys.day(date), 'vouchers'],
      queryFn: ({ signal }) =>
        api.get<TallyDayVouchers>(`${BASE}/days/${date}/vouchers`, { signal, timeoutMs: 60_000 }),
    }),
  dayCheck: (date: string) =>
    queryOptions({
      queryKey: [...tallyKeys.day(date), 'check'],
      queryFn: ({ signal }) => api.get<TallyDayCheck>(`${BASE}/days/${date}/check`, { signal, timeoutMs: 60_000 }),
    }),
  ledgers: () =>
    queryOptions({
      queryKey: tallyKeys.ledgers,
      queryFn: ({ signal }) => api.get<TallyLedgerMap>(`${BASE}/ledgers`, { signal }),
    }),
};

export const tallyApi = {
  /** Queues the day book of `date` (or of every day up to `to`) for posting; vouchers already in Tally are altered, not duplicated. */
  queue: (date: string, to?: string) => api.post<void>(`${BASE}/exports`, { date, to: to ?? null }),
  exportFile: (date: string) => api.download(`${BASE}/export-file`, { query: { date }, timeoutMs: 120_000 }),
  markImported: (date: string) => api.post<TallyExportDto>(`${BASE}/days/${date}/imported`),
  /** Checks the day against its records, and against Tally too when asked (reading Tally can take a while). */
  check: (date: string, againstTally: boolean) =>
    api.post<TallyDayCheck>(`${BASE}/days/${date}/check`, undefined, { query: { againstTally }, timeoutMs: 120_000 }),
  saveLedgers: (map: TallyLedgerMap) => api.put<void>(`${BASE}/ledgers`, map),
};
