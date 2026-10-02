import { HOSPITAL_TIME_ZONE } from '@hc360/core';
import type { NumberResetPolicy, NumberSeriesItem } from './api';

/**
 * Number formats as pure functions, mirroring the server (`Platform/Numbering/NumberFormat.cs` and
 * `NumberFormatRules.cs`) so the editor can show the next number and the problems while the administrator types. The
 * messages are the server's, word for word; the server checks again when the format is saved.
 */

export type FormatPartKind =
  'text' | 'facilityCode' | 'year' | 'shortYear' | 'month' | 'day' | 'sequence' | 'unknown' | 'unclosed';

/** A piece of a format: fixed text, a part in braces, or something in braces a number cannot have. */
export interface FormatPart {
  kind: FormatPartKind;
  /** The characters as written; for a part in braces, with its braces. */
  text: string;
  /** For the running number, the digits it is padded to ({SEQ:6} → 6). */
  width?: number;
}

/** The longest format the database keeps. */
export const FORMAT_COLUMN_LENGTH = 64;
export const MAX_SEQUENCE_WIDTH = 12;
/** A running number is measured as at least this many digits: it grows past its padding over the years. */
export const ASSUMED_SEQUENCE_DIGITS = 8;
export const MAX_FACILITY_CODE_LENGTH = 6;

/** The parts the format buttons insert, with what they stand for. */
export const FORMAT_TOKENS: readonly { token: string; label: string; meaning: string }[] = [
  { token: '{FAC}', label: '{FAC}', meaning: 'Facility code' },
  { token: '{YYYY}', label: '{YYYY}', meaning: 'Year, 4 digits' },
  { token: '{YY}', label: '{YY}', meaning: 'Year, 2 digits' },
  { token: '{MM}', label: '{MM}', meaning: 'Month, 2 digits' },
  { token: '{DD}', label: '{DD}', meaning: 'Day of the month, 2 digits' },
  { token: '{SEQ:6}', label: '{SEQ:n}', meaning: 'Running number, padded with zeros to n digits' },
];

export const RESTART_LABELS: Record<NumberResetPolicy, string> = {
  Never: 'Never',
  Yearly: 'Every year',
  Monthly: 'Every month',
  Daily: 'Every day',
};

/** The choices of when numbers start again from 1. */
export const RESTART_OPTIONS: readonly { value: NumberResetPolicy; label: string }[] = [
  { value: 'Never', label: 'Never' },
  { value: 'Yearly', label: 'Every year, on 1 January' },
  { value: 'Monthly', label: 'Every month, on the 1st' },
  { value: 'Daily', label: 'Every day, at midnight' },
];

/** "starts again every year", "never starts again". */
export function restartSentence(policy: NumberResetPolicy): string {
  return policy === 'Never' ? 'never starts again' : `starts again ${RESTART_LABELS[policy].toLowerCase()}`;
}

const NAMED_PARTS: Record<string, FormatPartKind> = {
  FAC: 'facilityCode',
  YYYY: 'year',
  YY: 'shortYear',
  MM: 'month',
  DD: 'day',
  SEQ: 'sequence',
};

function classify(raw: string): FormatPart {
  const name = raw.slice(1, -1);
  const kind = NAMED_PARTS[name];
  if (kind) {
    return { kind, text: raw };
  }
  // {SEQ:n}: n is checked by the rules (1 to 12); anything but digits after the colon is not a part.
  const digits = name.startsWith('SEQ:') ? name.slice(4) : '';
  return digits.length > 0 && digits.length <= 9 && /^[0-9]+$/.test(digits)
    ? { kind: 'sequence', text: raw, width: Number(digits) }
    : { kind: 'unknown', text: raw };
}

/** Splits a format into fixed text and parts in braces. Never fails: what is wrong comes back as a part. */
export function parseFormat(format: string): FormatPart[] {
  const parts: FormatPart[] = [];
  let text = '';
  let index = 0;
  while (index < format.length) {
    if (format[index] !== '{') {
      text += format[index];
      index++;
      continue;
    }
    if (text) {
      parts.push({ kind: 'text', text });
      text = '';
    }
    const close = format.indexOf('}', index + 1);
    if (close < 0) {
      parts.push({ kind: 'unclosed', text: format.slice(index) });
      return parts;
    }
    parts.push(classify(format.slice(index, close + 1)));
    index = close + 1;
  }
  if (text) {
    parts.push({ kind: 'text', text });
  }
  return parts;
}

/** A calendar date in the hospital's time zone (India Standard Time), as numbers are dated. */
export interface HospitalDate {
  year: number;
  month: number;
  day: number;
}

const isoDate = new Intl.DateTimeFormat('en-CA', {
  timeZone: HOSPITAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

export function hospitalDate(now: Date = new Date()): HospitalDate {
  const [year = 1970, month = 1, day = 1] = isoDate.format(now).split('-').map(Number);
  return { year, month, day };
}

/** "yyyy-MM-dd", as `formatDateOnly` takes it. */
export function toIsoDate(date: HospitalDate): string {
  return `${date.year}-${String(date.month).padStart(2, '0')}-${String(date.day).padStart(2, '0')}`;
}

/** The day numbers start again from 1, after `today`; null when they never do. */
export function nextRestart(policy: NumberResetPolicy, today: HospitalDate): HospitalDate | null {
  switch (policy) {
    case 'Yearly':
      return { year: today.year + 1, month: 1, day: 1 };
    case 'Monthly':
      return today.month === 12
        ? { year: today.year + 1, month: 1, day: 1 }
        : { ...today, month: today.month + 1, day: 1 };
    case 'Daily': {
      const next = new Date(Date.UTC(today.year, today.month - 1, today.day + 1));
      return { year: next.getUTCFullYear(), month: next.getUTCMonth() + 1, day: next.getUTCDate() };
    }
    default:
      return null;
  }
}

const two = (value: number) => String(value % 100).padStart(2, '0');

/** The number `format` makes for running number `value`. Anything the format may not contain is written as it is. */
export function renderNumber(format: string, value: number, facilityCode: string | null, date: HospitalDate): string {
  return parseFormat(format)
    .map((part) => {
      switch (part.kind) {
        case 'facilityCode':
          return facilityCode ?? '';
        case 'year':
          return String(date.year).padStart(4, '0');
        case 'shortYear':
          return two(date.year);
        case 'month':
          return two(date.month);
        case 'day':
          return two(date.day);
        case 'sequence':
          // A width the rules refuse (saved before they existed) pads no further than they allow.
          return String(value).padStart(Math.min(part.width ?? 0, MAX_SEQUENCE_WIDTH), '0');
        default:
          return part.text;
      }
    })
    .join('');
}

/** "a", "a and b", "a, b and c". */
function joinWords(words: readonly string[], conjunction: string): string {
  if (words.length <= 1) {
    return words[0] ?? '';
  }
  return `${words.slice(0, -1).join(', ')} ${conjunction} ${words[words.length - 1]}`;
}

const isFixedTextCharacter = (c: string) => /^[A-Z0-9/-]$/.test(c);

/** "small letters, spaces and ".": the characters in the order first seen, described for a reader. */
function describeCharacters(characters: readonly string[]): string {
  const words: string[] = [];
  for (const c of characters) {
    const word = /^[a-z]$/.test(c) ? 'small letters' : /^\s$/.test(c) ? 'spaces' : `"${c}"`;
    if (!words.includes(word)) {
      words.push(word);
    }
  }
  return joinWords(words, 'and');
}

/** What is wrong with how a format is written, most important first (empty when it is well formed). */
export function formatProblems(format: string | null | undefined): string[] {
  if (!format || !format.trim()) {
    return ['Enter a format, for example {FAC}{YY}{SEQ:6}.'];
  }
  const problems: string[] = [];
  if (format.length > FORMAT_COLUMN_LENGTH) {
    problems.push(`Use at most ${FORMAT_COLUMN_LENGTH} characters.`);
  }
  const parts = parseFormat(format);
  if (parts.some((p) => p.kind === 'unclosed')) {
    problems.push('A { has no closing }. Parts in braces look like {YY} or {SEQ:6}.');
  }
  const unknown = [...new Set(parts.filter((p) => p.kind === 'unknown').map((p) => p.text))];
  if (unknown.length > 0) {
    problems.push(
      `${joinWords(unknown, 'and')} ${unknown.length === 1 ? 'is not a part' : 'are not parts'} a number can have. ` +
        'Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.',
    );
  }
  const notAllowed = parts
    .filter((p) => p.kind === 'text')
    .flatMap((p) => [...p.text])
    .filter((c) => !isFixedTextCharacter(c));
  if (notAllowed.length > 0) {
    problems.push(
      `Outside the braces use only capital letters A to Z, digits, - and /. Not allowed: ${describeCharacters(notAllowed)}.`,
    );
  }
  const sequences = parts.filter((p) => p.kind === 'sequence');
  if (sequences.length === 0) {
    problems.push('Add {SEQ} or {SEQ:n} for the running number.');
  } else if (sequences.length > 1) {
    problems.push('Use the running number {SEQ} only once.');
  }
  if (sequences.some((s) => s.width !== undefined && (s.width < 1 || s.width > MAX_SEQUENCE_WIDTH))) {
    problems.push(`In {SEQ:n}, n is the number of digits and must be from 1 to ${MAX_SEQUENCE_WIDTH}.`);
  }
  return problems;
}

/** Why numbers that restart with `policy` cannot use `format` (the date of the period is missing); null when they can. */
export function resetProblem(format: string, policy: NumberResetPolicy): string | null {
  const kinds = new Set(parseFormat(format).map((p) => p.kind));
  const year = kinds.has('year') || kinds.has('shortYear');
  const month = kinds.has('month');
  const day = kinds.has('day');
  if (policy === 'Yearly' && !year) {
    return 'Numbers that start again every year must contain the year ({YY} or {YYYY}), or numbers from earlier years would be issued again.';
  }
  if (policy === 'Monthly' && !(year && month)) {
    return 'Numbers that start again every month must contain the year and the month ({YY} or {YYYY}, and {MM}), or earlier numbers would be issued again.';
  }
  if (policy === 'Daily' && !(year && month && day)) {
    return 'Numbers that start again every day must contain the year, the month and the day ({YY} or {YYYY}, {MM} and {DD}), or earlier numbers would be issued again.';
  }
  return null;
}

export interface NumberLength {
  facilityCode: number;
  date: number;
  runningNumber: number;
  fixedText: number;
  total: number;
}

/**
 * The longest number `format` can make: the facility code at `facilityCodeLength` letters, the date parts, the running
 * number at its padding or 8 digits, whichever is more, and the fixed text.
 */
export function longestNumber(format: string, facilityCodeLength: number): NumberLength {
  let facilityCode = 0;
  let date = 0;
  let runningNumber = 0;
  let fixedText = 0;
  for (const part of parseFormat(format)) {
    switch (part.kind) {
      case 'facilityCode':
        facilityCode += facilityCodeLength;
        break;
      case 'year':
        date += 4;
        break;
      case 'shortYear':
      case 'month':
      case 'day':
        date += 2;
        break;
      case 'sequence':
        runningNumber += Math.max(part.width ?? 0, ASSUMED_SEQUENCE_DIGITS);
        break;
      default:
        fixedText += part.text.length;
    }
  }
  return { facilityCode, date, runningNumber, fixedText, total: facilityCode + date + runningNumber + fixedText };
}

/**
 * The largest next number `format` allows: as many digits as the running number is measured with, or for a locked
 * series its padding, so its numbers keep their shape. Null when the format has no single, valid running number.
 */
export function largestStartAt(isLocked: boolean, format: string): number | null {
  const [sequence, ...more] = parseFormat(format).filter((p) => p.kind === 'sequence');
  if (!sequence || more.length > 0) {
    return null;
  }
  const width = sequence.width;
  if (width !== undefined && (width < 1 || width > MAX_SEQUENCE_WIDTH)) {
    return null;
  }
  const digits = isLocked ? (width ?? ASSUMED_SEQUENCE_DIGITS) : Math.max(width ?? 0, ASSUMED_SEQUENCE_DIGITS);
  return 10 ** digits - 1;
}

/** Another series' format now, and whether that is the series' own default. */
export interface OtherFormat {
  name: string;
  format: string;
  isDefault: boolean;
}

/** What the administrator asks to change in one series, and what the rules need to know about the hospital. */
export interface FormatChange {
  name: string;
  perFacility: boolean;
  isLocked: boolean;
  maxLength: number;
  defaultFormat: string;
  format: string;
  resetPolicy: NumberResetPolicy;
  startAt: number | null;
  currentFormat: string;
  currentResetPolicy: NumberResetPolicy;
  lastNumber: number;
  facilityCount: number;
  longestFacilityCode: number;
  otherFormats: readonly OtherFormat[];
}

export type FormatField = 'format' | 'resetPolicy' | 'startAt';

export type FormatErrors = Partial<Record<FormatField, string[]>>;

function tooLong(change: FormatChange, parts: FormatPart[], length: NumberLength): string {
  const pieces: string[] = [];
  if (length.facilityCode > 0) {
    pieces.push(`facility code ${length.facilityCode}`);
  }
  if (length.date > 0) {
    pieces.push(`date ${length.date}`);
  }
  pieces.push(`running number ${length.runningNumber}`);
  if (length.fixedText > 0) {
    pieces.push(`fixed text ${length.fixedText}`);
  }
  const fixes: string[] = [];
  if (length.fixedText > 0) {
    fixes.push('shorten the fixed text');
  }
  if (parts.some((p) => p.kind === 'year')) {
    fixes.push('use {YY} instead of {YYYY}');
  }
  if (parts.some((p) => p.kind === 'sequence' && (p.width ?? 0) > ASSUMED_SEQUENCE_DIGITS)) {
    fixes.push('use fewer digits in {SEQ:n}');
  }
  if (!change.perFacility && length.facilityCode > 0) {
    fixes.push('leave out {FAC}');
  }
  const joined = joinWords(fixes, 'or');
  const fix = fixes.length === 0 ? 'Leave out a part.' : `${joined.charAt(0).toUpperCase()}${joined.slice(1)}.`;
  return `Numbers in this format can be up to ${length.total} characters long (${pieces.join(', ')}), and only ${change.maxLength} fit. ${fix}`;
}

const PERIOD_WORDS: Record<NumberResetPolicy, string> = {
  Never: '',
  Yearly: ' this year',
  Monthly: ' this month',
  Daily: ' today',
};

/** The other series a changed format must differ from: those of this facility and the shared ones (the list's). */
export function otherFormats(series: readonly NumberSeriesItem[], key: string): OtherFormat[] {
  return series
    .filter((s) => s.key !== key)
    .map((s) => ({ name: s.name, format: s.format, isDefault: s.format === s.defaultFormat }));
}

const FACILITY_CODE_NEEDED =
  'Add {FAC}. Each facility counts its own numbers, so without the facility code two facilities would issue the same numbers.';

/** True when the facility code is missing although two facilities count these numbers separately. */
function needsFacilityCode(change: FormatChange, parts: FormatPart[]): boolean {
  return change.perFacility && change.facilityCount > 1 && !parts.some((p) => p.kind === 'facilityCode');
}

function lengthOf(change: FormatChange): NumberLength {
  return longestNumber(change.format, Math.min(Math.max(change.longestFacilityCode, 0), MAX_FACILITY_CODE_LENGTH));
}

/**
 * How the numbers of `format` read with every run of digits (the date parts, the running number and digits in the
 * fixed text) taken as one place, "#": {FAC}OB{YY}{SEQ:7}, {FAC}OB{SEQ:9} and {FAC}OB26{SEQ} all read "{FAC}OB#". Two
 * formats that read alike can make the same number: once a running number outgrows its padding ({SEQ:6} reaching
 * 1000000 matches {SEQ:7}), in the year written into one of them, or as {SEQ} and {SEQ:1}.
 */
export function outline(format: string): string {
  let result = '';
  const digits = () => {
    if (!result.endsWith('#')) {
      result += '#';
    }
  };
  for (const part of parseFormat(format)) {
    switch (part.kind) {
      case 'year':
      case 'shortYear':
      case 'month':
      case 'day':
      case 'sequence':
        digits();
        break;
      case 'text':
        for (const c of part.text) {
          if (/^[0-9]$/.test(c)) {
            digits();
          } else {
            result += c;
          }
        }
        break;
      default:
        result += part.text;
    }
  }
  return result;
}

/**
 * The other series a changed format would be confused with: one whose format reads alike. Two defaults that look
 * alike were chosen that way (they are kept in different tables), so restoring a default is always allowed.
 */
function sameFormatAs(change: FormatChange): OtherFormat | undefined {
  if (change.format === change.currentFormat) {
    return undefined;
  }
  const shape = outline(change.format);
  return change.otherFormats.find(
    (other) => outline(other.format) === shape && !(other.isDefault && change.format === change.defaultFormat),
  );
}

const sameFormatMessage = (other: OtherFormat, format: string) =>
  other.format === format
    ? `${other.name} already uses this format. Change the fixed text so the two kinds of number can be told apart.`
    : `${other.name} uses ${other.format}, which can make the same numbers: the two differ only in digits, dates or ` +
      'the length of the running number. Change the letters, - or / so the two kinds of number can be told apart.';

/**
 * Everything that stops the change from being saved, by field (empty when it can be saved). The format is checked in
 * full when it or the reset changes; only the next number otherwise. A locked series' format cannot change at all.
 */
export function checkChange(change: FormatChange): FormatErrors {
  const errors: FormatErrors = {};
  const add = (field: FormatField, message: string) => {
    (errors[field] ??= []).push(message);
  };
  const formatChanged = change.format !== change.currentFormat;
  const resetChanged = change.resetPolicy !== change.currentResetPolicy;
  const syntax = formatProblems(change.format);
  syntax.forEach((problem) => add('format', problem));

  if (syntax.length === 0 && (formatChanged || resetChanged)) {
    const reset = resetProblem(change.format, change.resetPolicy);
    if (reset) {
      add('resetPolicy', reset);
    }
    const parts = parseFormat(change.format);
    if (needsFacilityCode(change, parts)) {
      add('format', FACILITY_CODE_NEEDED);
    }
    const length = lengthOf(change);
    if (length.total > change.maxLength) {
      add('format', tooLong(change, parts, length));
    }
    const same = sameFormatAs(change);
    if (same) {
      add('format', sameFormatMessage(same, change.format));
    }
  }

  if (change.startAt !== null) {
    const largest = syntax.length === 0 ? largestStartAt(change.isLocked, change.format) : null;
    if (change.startAt < 1) {
      add('startAt', 'Enter a number of 1 or more.');
    } else if (change.startAt <= change.lastNumber) {
      add(
        'startAt',
        `Number ${change.lastNumber} has already been issued${PERIOD_WORDS[change.currentResetPolicy]}. Enter a number above ${change.lastNumber}.`,
      );
    } else if (largest !== null && change.startAt > largest) {
      add(
        'startAt',
        change.isLocked
          ? `The next number can be at most ${largest}, so that these numbers keep the shape other systems read.`
          : `The next number can have at most ${String(largest).length} digits.`,
      );
    }
  }
  return errors;
}

export interface FormatCheck {
  ok: boolean;
  text: string;
}

const RESET_KEEPS_NUMBERS_UNIQUE: Partial<Record<NumberResetPolicy, string>> = {
  Yearly: 'Contains the year, so numbers stay unique when they start again each year.',
  Monthly: 'Contains the year and the month, so numbers stay unique when they start again each month.',
  Daily: 'Contains the date, so numbers stay unique when they start again each day.',
};

/**
 * The rules as a checklist for the editor, passed or not, in the server's words: what a well-formed format needs for
 * its reset, the facility code, its length, and a difference from the other formats.
 */
export function formatChecks(change: FormatChange): FormatCheck[] {
  const problems = formatProblems(change.format);
  if (problems.length > 0) {
    return problems.map((text) => ({ ok: false, text }));
  }
  const checks: FormatCheck[] = [];
  const parts = parseFormat(change.format);
  const reset = resetProblem(change.format, change.resetPolicy);
  const resetOk = RESET_KEEPS_NUMBERS_UNIQUE[change.resetPolicy];
  if (reset) {
    checks.push({ ok: false, text: reset });
  } else if (resetOk) {
    checks.push({ ok: true, text: resetOk });
  }
  if (change.perFacility && change.facilityCount > 1) {
    checks.push(
      needsFacilityCode(change, parts)
        ? { ok: false, text: FACILITY_CODE_NEEDED }
        : { ok: true, text: 'Contains the facility code, so no two facilities issue the same number.' },
    );
  }
  const length = lengthOf(change);
  checks.push(
    length.total > change.maxLength
      ? { ok: false, text: tooLong(change, parts, length) }
      : {
          ok: true,
          text: `Numbers are at most ${length.total} characters long, and there is room for ${change.maxLength}.`,
        },
  );
  const same = sameFormatAs(change);
  checks.push(
    same
      ? { ok: false, text: sameFormatMessage(same, change.format) }
      : { ok: true, text: 'Different from the other number formats.' },
  );
  return checks;
}

/** "123 this year", "None yet": how many numbers the series has issued in its current period. */
export function lastIssuedText(lastNumber: number, policy: NumberResetPolicy): string {
  const period = PERIOD_WORDS[policy].trim();
  if (lastNumber > 0) {
    return period ? `${lastNumber} ${period}` : `${lastNumber}`;
  }
  return period ? `None ${period}` : 'None yet';
}

/**
 * The format with `token` typed over the selection `start`–`end`, and what to select afterwards: the place after the
 * token, or for {SEQ:6} the 6, so typing a digit replaces it.
 */
export function insertToken(
  value: string,
  start: number,
  end: number,
  token: string,
): { value: string; selectionStart: number; selectionEnd: number } {
  const from = Math.max(0, Math.min(start, value.length));
  const to = Math.max(from, Math.min(end, value.length));
  const next = value.slice(0, from) + token + value.slice(to);
  const after = from + token.length;
  const digits = /^\{SEQ:(\d+)\}$/.exec(token)?.[1];
  return digits
    ? { value: next, selectionStart: after - 1 - digits.length, selectionEnd: after - 1 }
    : { value: next, selectionStart: after, selectionEnd: after };
}
