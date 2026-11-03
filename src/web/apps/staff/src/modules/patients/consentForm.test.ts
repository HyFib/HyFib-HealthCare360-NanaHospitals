import { ApiError, todayIso } from '@hc360/core';
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
      sharingScope: null,
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

describe('what a record-sharing consent covers (gap #28)', () => {
  const sharing = (patch: Partial<ConsentFormValues>) =>
    values({ type: 'DataSharing', decision: 'given', sharing: 'limited', ...patch });

  it('asks what a limited consent covers, and refuses one limited to nothing', () => {
    expect(errors(sharing({}))).toEqual({
      sharing: 'Choose what the patient agreed to share, or share the whole record.',
    });
  });

  it('sends the partners, purposes, kinds and dates the patient chose, and leaves the rest open', () => {
    const v = sharing({
      shareWith: ['0192a1b2-0000-7000-8000-00000000a001'],
      purposes: ['Treatment', 'EmergencyTreatment'],
      recordsFrom: '2026-01-01',
      until: todayIso(30),
    });
    expect(errors(v)).toEqual({});
    expect(toCaptureRequest(consentFormSchema.parse(v)).sharingScope).toEqual({
      shareWith: ['0192a1b2-0000-7000-8000-00000000a001'],
      purposes: ['Treatment', 'EmergencyTreatment'],
      kinds: null,
      recordsFrom: '2026-01-01',
      recordsTo: null,
      until: todayIso(30),
    });
    // Only the kinds chosen: every account and any purpose stay open.
    expect(toCaptureRequest(consentFormSchema.parse(sharing({ kinds: ['Allergies'] }))).sharingScope).toEqual({
      shareWith: null,
      purposes: null,
      kinds: ['Allergies'],
      recordsFrom: null,
      recordsTo: null,
      until: null,
    });
  });

  it('sends no scope for the whole record, a refusal, or another consent', () => {
    const chosen = { kinds: ['LabResults' as const], sharing: 'limited' as const };
    expect(toCaptureRequest(values({ type: 'DataSharing', ...chosen, sharing: 'everything' })).sharingScope).toBeNull();
    expect(toCaptureRequest(values({ type: 'DataSharing', decision: 'refused', ...chosen })).sharingScope).toBeNull();
    expect(toCaptureRequest(values({ type: 'Research', ...chosen })).sharingScope).toBeNull();
    expect(errors(values({ type: 'DataSharing', decision: 'refused', sharing: 'limited' }))).toEqual({});
  });

  it('refuses records ending before they start, and a last day that has passed', () => {
    const e = errors(sharing({ recordsFrom: '2026-06-01', recordsTo: '2026-05-31', until: todayIso(-1) }));
    expect(e.recordsTo).toBe('Choose a day on or after the first day.');
    expect(e.until).toBe('Choose today or a later day.');
    expect(errors(sharing({ until: todayIso() }))).toEqual({});
  });

  it('puts the server’s messages about the scope on their fields', () => {
    const error = new ApiError({
      status: 400,
      title: 'Please correct the highlighted fields.',
      errors: { 'sharingScope.recordsTo': ['The records’ last day must be on or after their first day.'] },
    });
    expect(consentServerErrors(error).fields).toEqual([
      ['recordsTo', 'The records’ last day must be on or after their first day.'],
    ]);
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
