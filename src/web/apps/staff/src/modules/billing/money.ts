/**
 * Exact rupee arithmetic for billing previews, payment splits and validation.
 *
 * The server keeps money in C# `decimal` and rounds half away from zero (`Money.Round`, `Money.PercentOf`,
 * `Money.SplitGst`). Doing the same sums with JavaScript floating point would sometimes be a paisa out (0.1 + 0.2), so
 * every calculation here works on integers: amounts in paise and percentages in hundredths of a percent (`bigint`
 * inside the maths), and every rounding is half away from zero on the exact decimal value.
 */

/** Largest amount the server accepts in one field (₹10 crore), see BillingValidation.MaxAmount. */
export const MAX_AMOUNT_RUPEES = 100_000_000;

const PAISE_PER_RUPEE = 100n;
/** 100 % expressed in hundredths of a percent. */
const WHOLE_PERCENT = 10_000n;

interface PlainDecimal {
  negative: boolean;
  integer: string;
  fraction: string;
}

/**
 * The decimal digits a number was written with. JSON numbers from the server are decimals; `String()` returns the
 * shortest text that maps back to the same double, which is exactly the decimal the server sent.
 */
function plainDecimal(value: number): PlainDecimal {
  if (!Number.isFinite(value)) {
    throw new RangeError(`Not a finite amount: ${value}`);
  }
  const text = String(Math.abs(value));
  const match = /^(\d+)(?:\.(\d+))?(?:e([+-]\d+))?$/i.exec(text);
  if (!match) {
    throw new RangeError(`Unsupported number format: ${text}`);
  }
  const mantissaInteger = match[1] ?? '0';
  const mantissaFraction = match[2] ?? '';
  const exponent = Number(match[3] ?? '0');
  const digits = mantissaInteger + mantissaFraction;
  const point = mantissaInteger.length + exponent;
  let integer: string;
  let fraction: string;
  if (point <= 0) {
    integer = '0';
    fraction = '0'.repeat(-point) + digits;
  } else if (point >= digits.length) {
    integer = digits + '0'.repeat(point - digits.length);
    fraction = '';
  } else {
    integer = digits.slice(0, point);
    fraction = digits.slice(point);
  }
  return { negative: value < 0, integer, fraction };
}

/** Scales a decimal number to an integer with `decimals` places, rounding half away from zero (C# `Math.Round`). */
export function scaleDecimal(value: number, decimals: number): bigint {
  const { negative, integer, fraction } = plainDecimal(value);
  const kept = (fraction + '0'.repeat(decimals)).slice(0, decimals);
  const next = fraction.charAt(decimals);
  let scaled = BigInt(integer + kept);
  if (next !== '' && next >= '5') {
    scaled += 1n;
  }
  return negative && scaled !== 0n ? -scaled : scaled;
}

/** Rupees → paise, rounded half away from zero like `Money.Round`. */
export function toPaise(rupees: number): bigint {
  return scaleDecimal(rupees, 2);
}

/** Percentage → hundredths of a percent (12.5 % → 1250). */
export function toCentiPercent(percent: number): bigint {
  return scaleDecimal(percent, 2);
}

/** Paise → rupees as a JSON-safe number (the nearest double to the exact 2-decimal amount). */
export function fromPaise(paise: bigint): number {
  return Number(paise) / 100;
}

/** Integer division rounded half away from zero. */
export function roundDiv(numerator: bigint, denominator: bigint): bigint {
  if (denominator === 0n) {
    throw new RangeError('Division by zero');
  }
  const negative = numerator < 0n !== denominator < 0n;
  const n = numerator < 0n ? -numerator : numerator;
  const d = denominator < 0n ? -denominator : denominator;
  let quotient = n / d;
  if ((n % d) * 2n >= d) {
    quotient += 1n;
  }
  return negative && quotient !== 0n ? -quotient : quotient;
}

/** `Money.PercentOf`: a percentage of an amount, rounded to the paisa. */
export function percentOfPaise(amount: bigint, centiPercent: bigint): bigint {
  return roundDiv(amount * centiPercent, WHOLE_PERCENT);
}

/** `Money.SplitGst`: equal CGST and SGST; SGST takes any rounding paisa so the two always add back to the GST. */
export function splitGstPaise(tax: bigint): { cgst: bigint; sgst: bigint } {
  const cgst = roundDiv(tax, 2n);
  return { cgst, sgst: tax - cgst };
}

/** Rounds paise to the nearest whole rupee, half away from zero (the patient share collected in cash). */
export function roundToRupee(paise: bigint): bigint {
  return roundDiv(paise, PAISE_PER_RUPEE) * PAISE_PER_RUPEE;
}

export function minBig(a: bigint, b: bigint): bigint {
  return a < b ? a : b;
}

export function maxBig(a: bigint, b: bigint): bigint {
  return a > b ? a : b;
}

export function clampBig(value: bigint, min: bigint, max: bigint): bigint {
  return value < min ? min : value > max ? max : value;
}

export function absBig(value: bigint): bigint {
  return value < 0n ? -value : value;
}

/** Exact sum of rupee amounts. */
export function sumRupees(values: readonly number[]): number {
  return fromPaise(values.reduce((total, value) => total + toPaise(value), 0n));
}

/** Exact `a − b` in rupees. */
export function subtractRupees(a: number, b: number): number {
  return fromPaise(toPaise(a) - toPaise(b));
}

/** True when a rupee amount has at most two decimal places (what the server's validators accept). */
export function hasAtMostTwoDecimals(value: number): boolean {
  return Number.isFinite(value) && plainDecimal(value).fraction.replace(/0+$/, '').length <= 2;
}

export type ParsedAmount = { ok: true; paise: bigint; rupees: number } | { ok: false; error: string };

/**
 * Reads an amount typed by a user ("1,250", "₹ 99.5", "100."). Digit grouping commas and the rupee sign are allowed;
 * more than two decimals, negatives and amounts above ₹10 crore are refused with the server's wording.
 */
export function parseRupees(text: string, options: { allowZero?: boolean; max?: number } = {}): ParsedAmount {
  const cleaned = text.replace(/[₹,\s]/g, '').replace(/^rs\.?/i, '');
  if (cleaned === '') {
    return { ok: false, error: 'Enter an amount.' };
  }
  const match = /^(\d+)(?:\.(\d*))?$/.exec(cleaned);
  if (!match) {
    return { ok: false, error: cleaned.startsWith('-') ? 'The amount cannot be negative.' : 'Enter the amount in rupees, for example 1250 or 99.50.' };
  }
  const fraction = match[2] ?? '';
  if (fraction.length > 2) {
    return { ok: false, error: 'Use at most 2 decimal places.' };
  }
  const paise = BigInt(match[1] ?? '0') * PAISE_PER_RUPEE + BigInt((fraction + '00').slice(0, 2));
  if (paise === 0n && !options.allowZero) {
    return { ok: false, error: 'Enter an amount greater than zero.' };
  }
  const max = options.max ?? MAX_AMOUNT_RUPEES;
  if (paise > toPaise(max)) {
    return { ok: false, error: 'The amount is too large.' };
  }
  return { ok: true, paise, rupees: fromPaise(paise) };
}

export type ParsedPercent = { ok: true; centi: bigint; percent: number } | { ok: false; error: string };

/** Reads a percentage between 0 and 100 with at most two decimals ("12.5", "10 %"). */
export function parsePercent(text: string, options: { allowZero?: boolean } = {}): ParsedPercent {
  const cleaned = text.replace(/[%\s]/g, '');
  if (cleaned === '') {
    return { ok: false, error: 'Enter a percentage.' };
  }
  const match = /^(\d+)(?:\.(\d*))?$/.exec(cleaned);
  if (!match) {
    return { ok: false, error: 'Enter a percentage between 0 and 100.' };
  }
  const fraction = match[2] ?? '';
  if (fraction.length > 2) {
    return { ok: false, error: 'Use at most 2 decimal places.' };
  }
  const centi = BigInt(match[1] ?? '0') * 100n + BigInt((fraction + '00').slice(0, 2));
  if (centi > WHOLE_PERCENT) {
    return { ok: false, error: 'Enter a percentage between 0 and 100.' };
  }
  if (centi === 0n && !options.allowZero) {
    return { ok: false, error: 'Enter a percentage greater than zero.' };
  }
  return { ok: true, centi, percent: Number(centi) / 100 };
}

/** Plain text of an amount for an input box ("1250.5" → "1250.50", whole rupees without decimals). */
export function amountInputText(rupees: number | null | undefined): string {
  if (rupees === null || rupees === undefined) {
    return '';
  }
  const paise = toPaise(rupees);
  const sign = paise < 0n ? '-' : '';
  const abs = absBig(paise);
  const whole = abs / PAISE_PER_RUPEE;
  const part = abs % PAISE_PER_RUPEE;
  return part === 0n ? `${sign}${whole}` : `${sign}${whole}.${part.toString().padStart(2, '0')}`;
}

/** "12.5%" / "10%" (at most two decimals, trailing zeros dropped). */
export function formatPercent(percent: number | null | undefined): string {
  if (percent === null || percent === undefined || Number.isNaN(percent)) {
    return '—';
  }
  const centi = toCentiPercent(percent);
  const whole = absBig(centi) / 100n;
  const part = (absBig(centi) % 100n).toString().padStart(2, '0').replace(/0+$/, '');
  return `${centi < 0n ? '-' : ''}${whole}${part ? `.${part}` : ''}%`;
}

const ONES = [
  '',
  'One',
  'Two',
  'Three',
  'Four',
  'Five',
  'Six',
  'Seven',
  'Eight',
  'Nine',
  'Ten',
  'Eleven',
  'Twelve',
  'Thirteen',
  'Fourteen',
  'Fifteen',
  'Sixteen',
  'Seventeen',
  'Eighteen',
  'Nineteen',
];
const TENS = ['', '', 'Twenty', 'Thirty', 'Forty', 'Fifty', 'Sixty', 'Seventy', 'Eighty', 'Ninety'];

function belowHundred(n: number): string {
  if (n < 20) {
    return ONES[n] ?? '';
  }
  const unit = n % 10;
  return `${TENS[Math.floor(n / 10)] ?? ''}${unit ? ` ${ONES[unit] ?? ''}` : ''}`;
}

function belowThousand(n: number): string {
  const hundreds = Math.floor(n / 100);
  const rest = n % 100;
  return [hundreds ? `${ONES[hundreds] ?? ''} Hundred` : '', rest ? belowHundred(rest) : ''].filter(Boolean).join(' ');
}

/** Whole number in words on the Indian system (thousand, lakh, crore). */
export function indianNumberInWords(value: bigint): string {
  if (value === 0n) {
    return 'Zero';
  }
  const crore = value / 10_000_000n;
  const lakh = Number((value % 10_000_000n) / 100_000n);
  const thousand = Number((value % 100_000n) / 1_000n);
  const rest = Number(value % 1_000n);
  const parts: string[] = [];
  if (crore > 0n) {
    parts.push(`${indianNumberInWords(crore)} Crore`);
  }
  if (lakh) {
    parts.push(`${belowHundred(lakh)} Lakh`);
  }
  if (thousand) {
    parts.push(`${belowHundred(thousand)} Thousand`);
  }
  if (rest) {
    parts.push(belowThousand(rest));
  }
  return parts.join(' ');
}

/** "Rupees One Lakh Twenty Thousand Five Hundred and Fifty Paise Only" — printed on bills and receipts. */
export function amountInWords(rupees: number): string {
  const paise = toPaise(rupees);
  const abs = absBig(paise);
  const whole = abs / PAISE_PER_RUPEE;
  const part = Number(abs % PAISE_PER_RUPEE);
  const words = `Rupees ${indianNumberInWords(whole)}${part ? ` and ${belowHundred(part)} Paise` : ''} Only`;
  return paise < 0n ? `Minus ${words}` : words;
}
