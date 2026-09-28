/**
 * Barcode scanner input. USB/Bluetooth scanners behave like a keyboard: they "type" the code very fast and finish with
 * Enter. Depending on the model they also send control characters, an AIM symbology prefix ("]C1") or Code 39 start/stop
 * asterisks. These helpers turn whatever arrives into clean codes and tell scanned input apart from typing.
 */

// eslint-disable-next-line no-control-regex -- scanners really do send these control characters
const CONTROL_CHARACTERS = /[\u0000-\u001F\u007F]/g;
/** AIM symbology identifier, e.g. "]C0" (Code 128), "]E0" (EAN-13), "]Q1" (QR). */
const AIM_PREFIX = /^\][A-Za-z][0-9A-Za-z]/;

/** One scanned code, cleaned: no control characters, no AIM prefix, no Code 39 asterisks, no surrounding spaces. */
export function normalizeScan(raw: string | null | undefined): string {
  let text = (raw ?? '').replace(CONTROL_CHARACTERS, '').trim();
  text = text.replace(AIM_PREFIX, '').trim();
  if (text.length > 2 && text.startsWith('*') && text.endsWith('*')) {
    text = text.slice(1, -1).trim();
  }
  return text;
}

/**
 * Several codes in one go (a pasted list, or a scanner in batch mode separating codes with Enter or Tab), cleaned and
 * de-duplicated in scan order. Spaces are separators only between sample barcodes, which never contain spaces.
 */
export function splitScans(raw: string | null | undefined): string[] {
  const pieces = (raw ?? '')
    .split(/[\r\n\t,;]+/)
    .flatMap((piece) => {
      const trimmed = piece.trim();
      return /^\d+(\s+\d+)+$/.test(trimmed) ? trimmed.split(/\s+/) : [trimmed];
    })
    .map(normalizeScan)
    .filter((code) => code.length > 0);
  return [...new Set(pieces)];
}

export type ScanKind = 'sample' | 'labAccession' | 'radiologyAccession' | 'cardiologyAccession' | 'text';

/**
 * What a scanned code probably is. Sample barcodes are numeric (series lab.sample, {SEQ:9}); accession numbers follow
 * {FAC}LB{YY}{SEQ:7}, {FAC}RD… and {FAC}CD…; anything else (UHID, name) is searched as text.
 */
export function classifyScan(code: string): ScanKind {
  const value = code.trim().toUpperCase();
  if (/^\d{6,20}$/.test(value)) {
    return 'sample';
  }
  if (/^[A-Z]{1,6}LB\d{9}$/.test(value)) {
    return 'labAccession';
  }
  if (/^[A-Z]{1,6}RD\d{9}$/.test(value)) {
    return 'radiologyAccession';
  }
  if (/^[A-Z]{1,6}CD\d{9}$/.test(value)) {
    return 'cardiologyAccession';
  }
  return 'text';
}

export function isSampleBarcode(code: string): boolean {
  return classifyScan(code) === 'sample';
}

/**
 * Tells a scanner burst from human typing by the gaps between keystrokes (a scanner types a character every few
 * milliseconds; people need 80 ms or more). Used to catch a tube barcode scanned into a result field by mistake.
 */
export class KeystrokeTimer {
  private times: number[] = [];

  constructor(private readonly maxGapMs = 35) {}

  /** Record a printable keystroke at time `now` (ms). A long pause starts a new burst; returns true when it did. */
  record(now: number): boolean {
    const last = this.times.at(-1);
    const started = last === undefined || now - last > this.maxGapMs * 4;
    if (started) {
      this.times = [];
    }
    this.times.push(now);
    return started;
  }

  reset(): void {
    this.times = [];
  }

  /** Number of keystrokes in the current burst. */
  get length(): number {
    return this.times.length;
  }

  /** True when at least `minLength` keystrokes arrived with every gap within the scanner threshold. */
  isScannerBurst(minLength = 6): boolean {
    if (this.times.length < minLength) {
      return false;
    }
    for (let i = 1; i < this.times.length; i++) {
      if (this.times[i]! - this.times[i - 1]! > this.maxGapMs) {
        return false;
      }
    }
    return true;
  }
}
