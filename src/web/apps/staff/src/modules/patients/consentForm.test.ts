import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import {
  consentFormDefaults,
  consentFormSchema,
  consentServerErrors,
  toCaptureRequest,
  type ConsentFormValues,
} from './consentForm';

const values = (patch: Partial<ConsentFormValues>): ConsentFormValues => ({ ...consentFormDefaults(), ...patch });

/** The messages the schema gives, by field. */
function errors(v: ConsentFormValues): Record<string, string> {
  const result = consentFormSchema.safeParse(v);
  if (result.success) {
    return {};
  }
  return Object.fromEntries(result.error.issues.map((issue) => [issue.path.join('.'), issue.message]));
}

describe('record consent form', () => {
  it('starts empty and needs the consent chosen', () => {
    expect(errors(consentFormDefaults())).toEqual({ type: 'Choose the consent.' });
  });

  it('sends a consent the patient signed, with empty fields as null', () => {
    const v = values({ type: 'DataSharing', witnessName: '  ', notes: ' Explained in Tamil. ' });
    expect(errors(v)).toEqual({});
    expect(toCaptureRequest(consentFormSchema.parse(v))).toEqual({
      type: 'DataSharing',
      consentVersion: null,
      granted: true,
      witnessName: null,
      signedByRelation: null,
      signedByName: null,
      documentFileId: null,
      notes: 'Explained in Tamil.',
    });
  });

  it('needs the name and relation of someone signing on the patient’s behalf', () => {
    const e = errors(values({ type: 'HighRiskProcedure', signer: 'other', signedByName: '   ' }));
    expect(e.signedByName).toBe('Enter the name of the person consenting on the patient’s behalf.');
    expect(e.signedByRelation).toBe('Enter how they are related to the patient.');

    const v = values({
      type: 'HighRiskProcedure',
      decision: 'refused',
      signer: 'other',
      signedByName: ' Ravi Kumar ',
      signedByRelation: 'Son',
      witnessName: 'Sister Mary',
      consentVersion: '2.1',
    });
    expect(errors(v)).toEqual({});
    expect(toCaptureRequest(consentFormSchema.parse(v))).toMatchObject({
      granted: false,
      signedByName: 'Ravi Kumar',
      signedByRelation: 'Son',
      witnessName: 'Sister Mary',
      consentVersion: '2.1',
    });
  });

  it('drops a proxy signer typed before switching back to the patient', () => {
    const v = values({ type: 'Telehealth', signer: 'patient', signedByName: 'Ravi', signedByRelation: 'son' });
    expect(toCaptureRequest(consentFormSchema.parse(v))).toMatchObject({ signedByName: null, signedByRelation: null });
  });

  it('keeps to the server’s limits', () => {
    const e = errors(values({ type: 'Research', consentVersion: 'x'.repeat(17), notes: 'n'.repeat(1001) }));
    expect(e.consentVersion).toBe('Use at most 16 characters.');
    expect(e.notes).toBe('Use at most 1000 characters.');
  });

  it('refuses to build a request without a consent type', () => {
    expect(() => toCaptureRequest(consentFormDefaults())).toThrow();
  });
});

describe('server errors on save', () => {
  it('puts validation messages on their fields and the rest above the buttons', () => {
    const error = new ApiError({
      status: 400,
      title: 'Please correct the highlighted fields.',
      errors: { signedByName: ['Enter the name.'], documentFileId: ['The file was not found.'] },
    });
    expect(consentServerErrors(error)).toEqual({
      fields: [['signedByName', 'Enter the name.']],
      other: ['The file was not found.'],
    });
  });

  it('shows any other failure above the buttons', () => {
    const restricted = new ApiError({
      status: 403,
      title: 'Not allowed',
      code: 'patient.restricted',
      detail: 'This is a restricted record.',
    });
    expect(consentServerErrors(restricted)).toEqual({ fields: [], other: ['This is a restricted record.'] });
    expect(consentServerErrors(new Error('boom'))).toEqual({ fields: [], other: ['Please try again.'] });
  });
});
