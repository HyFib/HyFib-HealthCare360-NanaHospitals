import { formatInr, type PayerType } from '@hc360/core';
import {
  PAYER_TYPE_LABEL,
  SERVICE_CATEGORY_LABEL,
  type PaymentMode,
  type ServiceCategory,
} from '@/modules/billing/labels';
import { formatPercent, parsePercent, parseRupees, sumRupees } from '@/modules/billing/money';
import type { AccrualDto, CreatePayoutRequest, SaveShareRuleRequest, ShareMethod, ShareRuleDto } from './api';

/*
 * Pure rules behind the doctor-fee screens (DFA-01..05): the default periods, how a sharing rule and a fee's rate read,
 * the fees' total, and what the payout and sharing-rule forms send. The server checks the requests again
 * (CreatePayoutRequestValidator, SaveShareRuleRequestValidator, DoctorPayout.MarkPaid).
 */

// ------------------------------------------------------------------ periods

/** First and last day of the calendar month before `today` (yyyy-mm-dd): the usual payout period. */
export function previousMonth(today: string): { from: string; to: string } {
  const [year, month] = today.split('-').map(Number) as [number, number];
  const start = new Date(Date.UTC(year, month - 2, 1));
  const end = new Date(Date.UTC(year, month - 1, 0));
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  return { from: iso(start), to: iso(end) };
}

/** First day of the month of `today` (yyyy-mm-dd): where a statement starts unless chosen. */
export function monthStart(today: string): string {
  return `${today.slice(0, 8)}01`;
}

/** Both days chosen, and the period ends on or after its start. */
export function isPeriod(from: string, to: string): boolean {
  return Boolean(from) && Boolean(to) && to >= from;
}

// ------------------------------------------------------------------ how rules and fees read

/** What a sharing rule applies to: one service, a category or every service, and for patients of which payer type. */
export function ruleScope(rule: ShareRuleDto): string {
  const what = rule.serviceItemId
    ? `Service ${rule.serviceCode ?? ''}`
    : rule.category
      ? SERVICE_CATEGORY_LABEL[rule.category]
      : 'Every service';
  return rule.payerType ? `${what} · ${PAYER_TYPE_LABEL[rule.payerType]} patients` : what;
}

/** A sharing rule's share: "40%" of the billed value, or "₹150.00 per unit". */
export function ruleShare(method: ShareMethod, value: number): string {
  return method === 'Percentage' ? formatPercent(value) : `${formatInr(value)} per unit`;
}

/** The rate a fee was earned at: "40%", or "₹150.00/unit". */
export function feeRate(method: ShareMethod, rate: number): string {
  return method === 'Percentage' ? formatPercent(rate) : `${formatInr(rate)}/unit`;
}

/** What fee lines add up to, exact to the paisa (reversals count negative). */
export function feeTotal(fees: readonly Pick<AccrualDto, 'shareAmount'>[]): number {
  return sumRupees(fees.map((f) => f.shareAmount));
}

// ------------------------------------------------------------------ payout form

export interface DeductionDraft {
  key: number;
  description: string;
  amount: string;
}

/** The "Prepare a doctor payout" form as typed. */
export interface PayoutForm {
  doctorId: string | null;
  from: string;
  to: string;
  /** Empty uses the hospital's TDS setting. */
  tds: string;
  deductions: readonly DeductionDraft[];
  notes: string;
}

export interface PayoutFormCheck {
  periodOk: boolean;
  /** Why the TDS percentage cannot be used; null when it can, or when it is left empty. */
  tdsError: string | null;
  /** A deduction has no description, or no valid amount. */
  deductionProblem: boolean;
  /** What to send, or null until the form is complete and valid. */
  request: CreatePayoutRequest | null;
}

/** Checks the payout form and builds the request: TDS 0–100 % with two decimals, every deduction described and priced. */
export function checkPayoutForm(form: PayoutForm): PayoutFormCheck {
  const tds = form.tds.trim() ? parsePercent(form.tds, { allowZero: true }) : null;
  const deductions = form.deductions.map((d) => ({ description: d.description.trim(), amount: parseRupees(d.amount) }));
  const deductionProblem = deductions.some((d) => !d.amount.ok || !d.description);
  const periodOk = isPeriod(form.from, form.to);
  const tdsError = tds && !tds.ok ? tds.error : null;
  const doctorId = form.doctorId;
  return {
    periodOk,
    tdsError,
    deductionProblem,
    request:
      doctorId !== null && periodOk && tdsError === null && !deductionProblem
        ? {
            practitionerId: doctorId,
            periodFrom: form.from,
            periodTo: form.to,
            deductions: deductions.length
              ? deductions.map((d) => ({ description: d.description, amount: d.amount.ok ? d.amount.rupees : 0 }))
              : null,
            tdsPercent: tds?.ok ? tds.percent : null,
            notes: form.notes.trim() || null,
          }
        : null,
  };
}

/** A doctor paid other than in cash needs the bank transfer, UPI or cheque reference recorded (as the server requires). */
export function needsPaymentReference(mode: PaymentMode, reference: string): boolean {
  return mode !== 'Cash' && !reference.trim();
}

// ------------------------------------------------------------------ sharing-rule form

export type RuleScope = 'all' | 'category' | 'service';

/** The sharing-rule form as typed. */
export interface RuleForm {
  /** Empty for the hospital-wide default. */
  doctorId: string | null;
  scope: RuleScope;
  serviceId: string | null;
  category: ServiceCategory | '';
  payerType: PayerType | '';
  method: ShareMethod;
  value: string;
  from: string;
  to: string;
  active: boolean;
  notes: string;
  /** The rule's version when it is edited; null for a new rule. */
  version: number | null;
}

export interface RuleFormCheck {
  /** Why the share cannot be used; null when it can, or while nothing is typed. */
  valueError: string | null;
  datesOk: boolean;
  /** What to send, or null until the form is complete and valid. */
  request: SaveShareRuleRequest | null;
}

/**
 * Checks the sharing-rule form and builds the request: a percentage of 0–100 or rupees per unit, a start date and an
 * end on or after it, and the service or category the scope names. Only what the scope names is sent.
 */
export function checkRuleForm(form: RuleForm): RuleFormCheck {
  const parsed =
    form.method === 'Percentage'
      ? parsePercent(form.value, { allowZero: true })
      : parseRupees(form.value, { allowZero: true });
  const value = parsed.ok ? ('percent' in parsed ? parsed.percent : parsed.rupees) : null;
  const datesOk = Boolean(form.from) && (!form.to || form.to >= form.from);
  const scopeOk = form.scope === 'all' || (form.scope === 'service' ? form.serviceId !== null : form.category !== '');
  return {
    valueError: form.value && !parsed.ok ? parsed.error : null,
    datesOk,
    request:
      value !== null && datesOk && scopeOk
        ? {
            practitionerId: form.doctorId,
            serviceItemId: form.scope === 'service' ? form.serviceId : null,
            category: form.scope === 'category' && form.category ? form.category : null,
            payerType: form.payerType || null,
            method: form.method,
            value,
            effectiveFrom: form.from,
            effectiveTo: form.to || null,
            isActive: form.active,
            notes: form.notes.trim() || null,
            version: form.version,
          }
        : null,
  };
}
