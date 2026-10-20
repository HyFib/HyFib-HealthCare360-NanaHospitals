/**
 * Checking a patient's identity with their Aadhaar fingerprint (SRS INT-BIO): the form's rules mirror
 * VerifyIdentityRequestValidator and AadhaarNumber on the server, and the words staff see for each check and for each
 * fingerprint reader problem. Pure (no React, no Fluent) so it is unit-tested.
 */
import { BridgeError, isApiError, type AadhaarCaptureSetup, type FingerprintCapture } from '@hc360/core';
import type { StatusTone } from '@hc360/ui';
import type { IdentityCheckDto, IdentityCheckOutcome } from './api';
import { isValidAadhaar } from './validation';

/** The device provider id of a training capture (`RdService.TrainingProviderId` on the server). */
export const TRAINING_DEVICE = 'HC360.TRAINING';

export type AadhaarNumberKind = 'aadhaar' | 'vid';

/**
 * An Aadhaar number (12 digits, the first 2 to 9, a Verhoeff check digit) or a Virtual ID (16 digits), with the spaces
 * and dashes printed on the card allowed; null for anything else. The same rule as `AadhaarNumber.Normalize`.
 */
export function aadhaarNumberKind(value: string): AadhaarNumberKind | null {
  if (!/^[\d -]+$/.test(value) || !/\d/.test(value)) {
    return null;
  }
  const digits = value.replace(/\D/g, '');
  if (digits.length === 12 && isValidAadhaar(digits)) {
    return 'aadhaar';
  }
  return digits.length === 16 ? 'vid' : null;
}

/**
 * Said before the check when the number given is plainly not the record's (its last 4 digits differ): the server would
 * refuse it. A Virtual ID cannot be compared.
 */
export function numberMismatch(value: string, recordLast4: string | null): string | null {
  if (!recordLast4 || aadhaarNumberKind(value) !== 'aadhaar') {
    return null;
  }
  return value.replace(/\D/g, '').endsWith(recordLast4)
    ? null
    : `The record has an Aadhaar number ending in ${recordLast4}. Check the number with the patient.`;
}

/** Why the check cannot be made yet, in the order staff fill the dialog; null when it can. */
export function missingStep(values: {
  aadhaarNumber: string;
  consent: boolean;
  pidData: string | null;
}): string | null {
  if (!aadhaarNumberKind(values.aadhaarNumber.trim())) {
    return 'Enter the 12-digit Aadhaar number or the 16-digit Virtual ID.';
  }
  if (!values.consent) {
    return 'Ask for the patient’s consent to Aadhaar authentication first.';
  }
  return values.pidData ? null : 'Capture the fingerprint first.';
}

export const OUTCOME: Record<IdentityCheckOutcome, { label: string; tone: StatusTone }> = {
  Verified: { label: 'Verified', tone: 'success' },
  NotVerified: { label: 'Not verified', tone: 'danger' },
  Failed: { label: 'Could not check', tone: 'warning' },
};

/** Which number was checked: only its last 4 digits are ever kept. */
export function numberText(check: Pick<IdentityCheckDto, 'usedVirtualId' | 'aadhaarLast4' | 'linkedAadhaar'>): string {
  return `${check.usedVirtualId ? 'Virtual ID' : 'Aadhaar number'} ending ${check.aadhaarLast4}${
    check.linkedAadhaar ? ', linked to the record' : ''
  }`;
}

/** The reader the fingerprint was captured with. */
export function deviceText(device: { providerId?: string | null; modelId?: string | null } | null): string | null {
  if (!device?.providerId) {
    return null;
  }
  if (device.providerId === TRAINING_DEVICE) {
    return 'Training capture (no reader)';
  }
  return device.modelId ? `${device.providerId} ${device.modelId}` : device.providerId;
}

/** What answered, and its reference for the check: UIDAI's response code, or the training simulator. */
export function referenceText(
  check: Pick<IdentityCheckDto, 'provider' | 'responseCode' | 'transactionId' | 'errorCode'>,
): string {
  const error = check.errorCode ? `, error ${check.errorCode}` : '';
  if (check.provider === 'simulator') {
    return `Training simulator, not UIDAI${error}`;
  }
  return `UIDAI ${check.responseCode ?? check.transactionId ?? ''}${error}`.trim();
}

/** What a capture is: its reader and quality, for staff to see before checking. */
export function captureText(capture: Pick<FingerprintCapture, 'quality' | 'device'>): string {
  const device = deviceText(capture.device);
  const quality = capture.quality === null ? null : `quality ${capture.quality}`;
  return ['Fingerprint captured', [device, quality].filter(Boolean).join(', ')].filter(Boolean).join(': ');
}

/** Why the fingerprint could not be captured, from HC360 Desktop's answer (`FingerprintReader.Code`). */
export function captureProblem(error: unknown, setup: Pick<AadhaarCaptureSetup, 'training'>): string {
  if (!(error instanceof BridgeError)) {
    return 'The fingerprint could not be captured. Try again.';
  }
  switch (error.code) {
    case 'not_desktop':
      return setup.training
        ? 'Fingerprints are captured in HC360 Desktop. For training here, use a training capture.'
        : 'Fingerprints are captured in HC360 Desktop, on a workstation with a UIDAI-certified fingerprint reader.';
    case 'method_not_found':
      return 'This HC360 Desktop cannot use a fingerprint reader yet. Ask IT to update it.';
    case 'timeout':
      return 'No finger was captured in time. Place the finger on the reader when it lights up, and capture again.';
    default:
      // HC360 Desktop words the reader's problems for staff: not found, not ready, busy, failed.
      return error.message;
  }
}

/** The words for a check the server refused before asking UIDAI, or could not make. */
export function verifyProblem(error: unknown): string {
  return isApiError(error) ? error.userMessage : 'The identity could not be checked. Try again.';
}
