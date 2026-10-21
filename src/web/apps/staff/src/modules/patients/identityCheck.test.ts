import { ApiError, BridgeError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import {
  aadhaarNumberKind,
  captureProblem,
  captureText,
  deviceText,
  missingStep,
  numberMismatch,
  numberText,
  referenceText,
  verifyProblem,
} from './identityCheck';

// A valid Aadhaar number (Verhoeff), as UIDAI's test numbers are.
const AADHAAR = '999941057058';

describe('the number the patient gives', () => {
  it('takes an Aadhaar number or a Virtual ID as printed on the card, as the server does', () => {
    expect(aadhaarNumberKind(AADHAAR)).toBe('aadhaar');
    expect(aadhaarNumberKind('9999 4105 7058')).toBe('aadhaar');
    expect(aadhaarNumberKind('9999-4105-7058')).toBe('aadhaar');
    expect(aadhaarNumberKind('9134 5678 9012 3456')).toBe('vid');
  });

  it('refuses what is not one', () => {
    expect(aadhaarNumberKind('999941057059')).toBeNull(); // the check digit is wrong
    expect(aadhaarNumberKind('199941057058')).toBeNull(); // no Aadhaar number starts with 0 or 1
    expect(aadhaarNumberKind('99994105705')).toBeNull();
    expect(aadhaarNumberKind('XXXX XXXX 7058')).toBeNull();
    expect(aadhaarNumberKind('9999.4105.7058')).toBeNull();
    expect(aadhaarNumberKind(' - ')).toBeNull();
    expect(aadhaarNumberKind('')).toBeNull();
  });

  it('warns when the number is plainly not the record’s, before the server refuses it', () => {
    expect(numberMismatch('9999 4105 7058', '7058')).toBeNull();
    expect(numberMismatch(AADHAAR, '1234')).toBe(
      'The record has an Aadhaar number ending in 1234. Check the number with the patient.',
    );
    expect(numberMismatch(AADHAAR, null)).toBeNull();
    expect(numberMismatch('9134 5678 9012 3456', '1234')).toBeNull(); // a Virtual ID cannot be compared
    expect(numberMismatch('9999', '1234')).toBeNull();
  });

  it('says what is still missing, in the order the dialog asks', () => {
    expect(missingStep({ aadhaarNumber: '', consent: false, pidData: null })).toBe(
      'Enter the 12-digit Aadhaar number or the 16-digit Virtual ID.',
    );
    expect(missingStep({ aadhaarNumber: AADHAAR, consent: false, pidData: '<PidData/>' })).toBe(
      'Ask for the patient’s consent to Aadhaar authentication first.',
    );
    expect(missingStep({ aadhaarNumber: AADHAAR, consent: true, pidData: null })).toBe(
      'Capture the fingerprint first.',
    );
    expect(missingStep({ aadhaarNumber: ` ${AADHAAR} `, consent: true, pidData: '<PidData/>' })).toBeNull();
  });
});

describe('what staff see of a check', () => {
  it('shows only the last 4 digits, and whether the check linked the number', () => {
    expect(numberText({ usedVirtualId: false, aadhaarLast4: '7058', linkedAadhaar: true })).toBe(
      'Aadhaar number ending 7058, linked to the record',
    );
    expect(numberText({ usedVirtualId: true, aadhaarLast4: '3456', linkedAadhaar: false })).toBe(
      'Virtual ID ending 3456',
    );
  });

  it('names the reader, and a training capture as one', () => {
    expect(deviceText({ providerId: 'MANTRA.AND', modelId: 'MFS110' })).toBe('MANTRA.AND MFS110');
    expect(deviceText({ providerId: 'HC360.TRAINING', modelId: 'TRAINING' })).toBe('Training capture (no reader)');
    expect(deviceText({ providerId: null, modelId: null })).toBeNull();
    expect(deviceText(null)).toBeNull();
  });

  it('gives UIDAI’s reference, and never passes the simulator off as UIDAI', () => {
    expect(
      referenceText({ provider: 'uidai', responseCode: 'a1b2c3', transactionId: 'HC360ABC', errorCode: null }),
    ).toBe('UIDAI a1b2c3');
    expect(
      referenceText({ provider: 'uidai', responseCode: 'a1b2c3', transactionId: 'HC360ABC', errorCode: '300' }),
    ).toBe('UIDAI a1b2c3, error 300');
    expect(
      referenceText({ provider: 'simulator', responseCode: 'SIMULATED', transactionId: 'HC360ABC', errorCode: '300' }),
    ).toBe('Training simulator, not UIDAI, error 300');
  });

  it('describes a capture by its reader and quality, never by its content', () => {
    expect(captureText({ quality: 72, device: deviceOf('MANTRA.AND', 'MFS110') })).toBe(
      'Fingerprint captured: MANTRA.AND MFS110, quality 72',
    );
    expect(captureText({ quality: null, device: null })).toBe('Fingerprint captured');
  });
});

describe('problems', () => {
  it('passes on HC360 Desktop’s words for the reader', () => {
    const message = 'No fingerprint reader was found on this computer. Check that it is plugged in.';
    expect(captureProblem(new BridgeError('no_reader', message), { training: false })).toBe(message);
  });

  it('explains where fingerprints can be captured', () => {
    expect(captureProblem(new BridgeError('not_desktop', '…'), { training: false })).toBe(
      'Fingerprints are captured in HC360 Desktop, on a workstation with a UIDAI-certified fingerprint reader.',
    );
    expect(captureProblem(new BridgeError('not_desktop', '…'), { training: true })).toContain('training capture');
    expect(captureProblem(new BridgeError('method_not_found', '…'), { training: false })).toBe(
      'This HC360 Desktop cannot use a fingerprint reader yet. Ask IT to update it.',
    );
    expect(captureProblem(new BridgeError('timeout', 'Desktop bridge call timed out.'), { training: false })).toContain(
      'capture again',
    );
    expect(captureProblem(new Error('boom'), { training: false })).toBe(
      'The fingerprint could not be captured. Try again.',
    );
  });

  it('gives the server’s reason for a check it refused', () => {
    const mismatch = new ApiError({
      status: 422,
      title: 'Unprocessable',
      code: 'patient.aadhaar_mismatch',
      detail: 'This is not the Aadhaar number on Asha’s record (it ends in 7058). Check the number with the patient.',
    });
    expect(verifyProblem(mismatch)).toBe(
      'This is not the Aadhaar number on Asha’s record (it ends in 7058). Check the number with the patient.',
    );
    const invalid = new ApiError({
      status: 400,
      title: 'One or more validation errors occurred.',
      errors: { consent: ['Ask for the patient’s consent to Aadhaar authentication first.'] },
    });
    expect(verifyProblem(invalid)).toBe('Ask for the patient’s consent to Aadhaar authentication first.');
    expect(verifyProblem(new Error('offline'))).toBe('The identity could not be checked. Try again.');
  });
});

function deviceOf(providerId: string, modelId: string) {
  return { providerId, modelId, serialNumber: null, serviceId: `${providerId}.WIN`, serviceVersion: '1.0' };
}
