import { describe, expect, it } from 'vitest';
import {
  classifyScan,
  isMistypedTubeNumber,
  isSampleBarcode,
  KeystrokeTimer,
  MISTYPED_TUBE_NUMBER,
  normalizeScan,
  splitScans,
} from './scan';

describe('normalizeScan', () => {
  it('removes what scanners add around the code', () => {
    expect(normalizeScan('000123456\r\n')).toBe('000123456');
    expect(normalizeScan('\u001d000123456\t')).toBe('000123456');
    expect(normalizeScan(']C1000123456')).toBe('000123456');
    expect(normalizeScan('*000123456*')).toBe('000123456');
    expect(normalizeScan('  NHLB260000123 ')).toBe('NHLB260000123');
  });

  it('keeps an empty scan empty', () => {
    expect(normalizeScan('\r\n')).toBe('');
    expect(normalizeScan(undefined)).toBe('');
    expect(normalizeScan('*')).toBe('*');
  });
});

describe('splitScans', () => {
  it('splits a rack of scans and drops repeats', () => {
    expect(splitScans('000000101\r\n000000102\n000000101\n')).toEqual(['000000101', '000000102']);
    expect(splitScans('000000101\t000000102')).toEqual(['000000101', '000000102']);
    expect(splitScans('000000101, 000000102;000000103')).toEqual(['000000101', '000000102', '000000103']);
    expect(splitScans('000000101 000000102')).toEqual(['000000101', '000000102']);
  });

  it('keeps words together', () => {
    expect(splitScans('Asha Rao')).toEqual(['Asha Rao']);
    expect(splitScans('')).toEqual([]);
  });
});

describe('classifyScan', () => {
  it('recognises sample barcodes and accession numbers', () => {
    expect(classifyScan('1000012342')).toBe('sample');
    expect(isSampleBarcode('1000012342')).toBe(true);
    expect(classifyScan(' 1000012350 ')).toBe('sample');
    // A tube numbered before the check digit still opens; so does a number typed wrong, which the server refuses.
    expect(classifyScan('1000000123')).toBe('sample');
    expect(classifyScan('000123456')).toBe('sample');
    expect(isSampleBarcode('000123456')).toBe(true);
    expect(classifyScan('NHLB260000123')).toBe('labAccession');
    expect(classifyScan('nhrd260000045')).toBe('radiologyAccession');
    expect(classifyScan('NHCD260000007')).toBe('cardiologyAccession');
    expect(classifyScan('NH26000123')).toBe('text');
    expect(classifyScan('12345')).toBe('text');
    expect(isSampleBarcode('Asha')).toBe(false);
  });
});

// The server's TubeLookupTests: tube numbers end in a check digit, so one digit typed wrong or two swapped never checks.
describe('isMistypedTubeNumber', () => {
  it('knows a tube number typed with a digit wrong or two neighbouring digits swapped', () => {
    expect(isMistypedTubeNumber('1000012342')).toBe(false);
    expect(isMistypedTubeNumber('1000012352')).toBe(true);
    expect(isMistypedTubeNumber('1000012324')).toBe(true);
    expect(isMistypedTubeNumber(' 1000012346 ')).toBe(true);
  });

  it('says nothing of what is not 10 digits', () => {
    expect(isMistypedTubeNumber('100001234')).toBe(false);
    expect(isMistypedTubeNumber('000123456')).toBe(false);
    expect(isMistypedTubeNumber('NHLB260000123')).toBe(false);
    expect(isMistypedTubeNumber('')).toBe(false);
  });

  it('says what to do', () => {
    expect(MISTYPED_TUBE_NUMBER).toBe('The last digit does not match the others: check the tube number.');
  });
});

describe('KeystrokeTimer', () => {
  it('recognises a scanner burst', () => {
    const timer = new KeystrokeTimer(35);
    [0, 8, 16, 25, 33, 41, 50, 58, 66].forEach((t) => timer.record(t));
    expect(timer.isScannerBurst(6)).toBe(true);
  });

  it('does not mistake typing for a scan', () => {
    const timer = new KeystrokeTimer(35);
    [0, 120, 260, 380, 500, 610].forEach((t) => timer.record(t));
    expect(timer.isScannerBurst(6)).toBe(false);
  });

  it('starts a new burst after a pause and needs enough characters', () => {
    const timer = new KeystrokeTimer(35);
    expect(timer.record(0)).toBe(true);
    expect(timer.record(150)).toBe(true);
    expect(timer.record(300)).toBe(true);
    expect(timer.record(1000)).toBe(true);
    expect(timer.record(1008)).toBe(false);
    [1016, 1024, 1032, 1040].forEach((t) => timer.record(t));
    expect(timer.length).toBe(6);
    expect(timer.isScannerBurst(6)).toBe(true);
    timer.reset();
    [0, 5, 10].forEach((t) => timer.record(t));
    expect(timer.isScannerBurst(6)).toBe(false);
  });
});
