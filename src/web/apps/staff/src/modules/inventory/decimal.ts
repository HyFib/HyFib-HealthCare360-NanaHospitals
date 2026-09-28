/**
 * Exact decimal arithmetic for money and quantity previews. The server works in C# `decimal` and rounds with
 * MidpointRounding.AwayFromZero (SharedKernel `Money`); JavaScript numbers are binary floating point, so
 * `1.005 * 100` or `0.1 + 0.2` would drift by a paisa. Values here are BigInts scaled by 10^8, which holds
 * quantities (3 dp), rates and MRPs (4 dp) and percentages (2 dp) exactly, so products and rounding match the
 * server to the paisa.
 */
export type Dec = bigint;

const PLACES = 8;
const SCALE = 10n ** BigInt(PLACES);

export const ZERO: Dec = 0n;
export const ONE: Dec = SCALE;
export const HUNDRED: Dec = 100n * SCALE;

function abs(value: bigint): bigint {
  return value < 0n ? -value : value;
}

/** n ÷ d rounded half away from zero (d ≠ 0). */
function divRoundInteger(n: bigint, d: bigint): bigint {
  const negative = n < 0n !== d < 0n;
  const an = abs(n);
  const ad = abs(d);
  const quotient = an / ad;
  const remainder = an % ad;
  const rounded = remainder * 2n >= ad ? quotient + 1n : quotient;
  return negative ? -rounded : rounded;
}

/** Plain (non-exponent) decimal text of a finite number. */
function plainText(value: number): string {
  if (!Number.isFinite(value)) {
    throw new RangeError(`Not a finite number: ${value}`);
  }
  const text = String(value);
  return /e/i.test(text) ? value.toFixed(PLACES + 4) : text;
}

/** Parses a number or decimal string exactly ("12.345", 0.1, "-3"). Blank and null are zero. */
export function dec(value: number | string | null | undefined): Dec {
  if (value === null || value === undefined) {
    return ZERO;
  }
  const text = typeof value === 'number' ? plainText(value) : value.trim();
  if (text === '') {
    return ZERO;
  }
  const match = /^([+-])?(\d*)(?:\.(\d*))?$/.exec(text);
  if (!match || (match[2] === '' && (match[3] ?? '') === '')) {
    throw new RangeError(`Not a decimal number: ${text}`);
  }
  const negative = match[1] === '-';
  const whole = match[2] || '0';
  const fraction = match[3] ?? '';
  const kept = fraction.slice(0, PLACES).padEnd(PLACES, '0');
  let scaled = BigInt(whole) * SCALE + BigInt(kept);
  const dropped = fraction.slice(PLACES);
  if (dropped !== '' && Number(dropped[0]) >= 5) {
    scaled += 1n;
  }
  return negative ? -scaled : scaled;
}

/** Rounds to `places` decimals, half away from zero (C# MidpointRounding.AwayFromZero). */
export function roundTo(value: Dec, places: number): Dec {
  if (places >= PLACES) {
    return value;
  }
  const unit = 10n ** BigInt(PLACES - places);
  return divRoundInteger(value, unit) * unit;
}

/** Money.Round: 2 decimals, half away from zero. */
export function round2(value: Dec): Dec {
  return roundTo(value, 2);
}

/** a × b (exact while the result needs at most 8 decimals, otherwise rounded half away from zero). */
export function mul(a: Dec, b: Dec): Dec {
  return divRoundInteger(a * b, SCALE);
}

/** a ÷ b computed exactly and rounded to `places` decimals, half away from zero. */
export function divRound(a: Dec, b: Dec, places: number): Dec {
  if (b === ZERO) {
    throw new RangeError('Division by zero');
  }
  const p = Math.min(places, PLACES);
  return divRoundInteger(a * 10n ** BigInt(p), b) * 10n ** BigInt(PLACES - p);
}

export function sum(values: readonly Dec[]): Dec {
  return values.reduce((total, value) => total + value, ZERO);
}

export function min(a: Dec, b: Dec): Dec {
  return a < b ? a : b;
}

export function max(a: Dec, b: Dec): Dec {
  return a > b ? a : b;
}

/** Exact text, trimmed of trailing zeros unless `places` asks for a fixed number of decimals. */
export function toText(value: Dec, places?: number): string {
  const rounded = places === undefined ? value : roundTo(value, places);
  const negative = rounded < 0n;
  const digits = abs(rounded).toString().padStart(PLACES + 1, '0');
  const whole = digits.slice(0, -PLACES);
  let fraction = digits.slice(-PLACES);
  fraction = places === undefined ? fraction.replace(/0+$/, '') : fraction.slice(0, Math.max(places, 0));
  const text = fraction ? `${whole}.${fraction}` : whole;
  return negative && /[1-9]/.test(text) ? `-${text}` : text;
}

/** Converts back to a JS number (for display and JSON); exact for values with up to 15 significant digits. */
export function toNumber(value: Dec): number {
  return Number(toText(value));
}

/** Convenience: exact sum of plain numbers, returned as a number. */
export function sumNumbers(values: readonly (number | null | undefined)[]): number {
  return toNumber(sum(values.map((v) => dec(v ?? 0))));
}

/** Convenience: exact a − b of plain numbers. */
export function subtractNumbers(a: number, b: number): number {
  return toNumber(dec(a) - dec(b));
}
