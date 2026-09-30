import { describe, expect, it } from 'vitest';
import { intakeDefaults, intakeFieldFor, intakeSchema, toCreateRequest, type IntakeValues } from './intake';

const patient = { id: '7c1f0e4e-0000-4000-8000-000000000001', uhid: 'NH0000123', fullName: 'Asha Rao' };

const values = (patch: Partial<IntakeValues>): IntakeValues => ({ ...intakeDefaults(), ...patch });

/** The messages the schema gives, by field path. */
function errors(v: IntakeValues): Record<string, string> {
  const result = intakeSchema.safeParse(v);
  if (result.success) {
    return {};
  }
  return Object.fromEntries(result.error.issues.map((issue) => [issue.path.join('.'), issue.message]));
}

describe('request intake', () => {
  it('needs the patient (or their name) and what they ask for', () => {
    const e = errors(intakeDefaults());
    expect(e.patient).toMatch(/Choose the patient/);
    expect(e.type).toBe('Choose what the person is asking for.');
  });

  it('accepts a registered patient asking at the desk', () => {
    const v = values({ patient, type: 'Access', details: 'Wants to know what we hold.' });
    expect(errors(v)).toEqual({});
    expect(toCreateRequest(intakeSchema.parse(v))).toEqual({
      type: 'Access',
      channel: 'FrontDesk',
      patientId: patient.id,
      principalName: null,
      contactPhone: null,
      contactEmail: null,
      requestedBy: 'Self',
      requesterName: null,
      details: 'Wants to know what we hold.',
      corrections: null,
      identityCheck: null,
      identityNote: null,
    });
  });

  it('needs a name and a way to reply for someone who is not a registered patient', () => {
    const e = errors(
      values({ notRegistered: true, type: 'Grievance', details: 'Received an SMS meant for someone else.' }),
    );
    expect(e.principalName).toMatch(/name of the person/);
    expect(e.contactPhone).toBe('Enter a phone number or e-mail address to reply to.');

    const v = values({
      notRegistered: true,
      patient,
      principalName: ' Ravi Kumar ',
      contactPhone: '098765 43210',
      type: 'Grievance',
      channel: 'Phone',
      details: 'Received an SMS meant for someone else.',
    });
    expect(errors(v)).toEqual({});
    const request = toCreateRequest(intakeSchema.parse(v));
    // The picked patient is ignored once the box is ticked.
    expect(request.patientId).toBeNull();
    expect(request.principalName).toBe('Ravi Kumar');
    expect(request.contactPhone).toBe('+919876543210');
  });

  it('checks the phone number and e-mail address', () => {
    const e = errors(
      values({ patient, type: 'Access', details: 'x', contactPhone: '12345', contactEmail: 'not-an-address' }),
    );
    expect(e.contactPhone).toBe('Enter a valid phone number.');
    expect(e.contactEmail).toBe('Enter a valid e-mail address.');
  });

  it('asks for the requester’s name when they are not the patient', () => {
    const v = values({ patient, type: 'Erasure', details: 'Delete my data.', requestedBy: 'Nominee' });
    expect(errors(v).requesterName).toBe('Enter the name of the person making the request.');
    const request = toCreateRequest(intakeSchema.parse({ ...v, requesterName: 'Meena Rao' }));
    expect(request.requesterName).toBe('Meena Rao');
    // The name is dropped when the patient asks for themselves.
    expect(
      toCreateRequest(intakeSchema.parse({ ...v, requestedBy: 'Self', requesterName: 'x' })).requesterName,
    ).toBeNull();
  });

  it('checks each change of a correction, and each detail once', () => {
    const e = errors(
      values({
        patient,
        type: 'Correction',
        corrections: [
          { field: 'name', requestedValue: 'Asha R. Rao' },
          { field: 'name', requestedValue: 'A. Rao' },
          { field: '', requestedValue: '' },
        ],
      }),
    );
    expect(e['corrections.1.field']).toBe('List each detail once.');
    expect(e['corrections.2.field']).toBe('Choose a detail from the list.');
    expect(e['corrections.2.requestedValue']).toBe('Enter the correct value.');
    // The listed changes are enough; the description is optional then.
    expect(e.details).toBeUndefined();
    expect(errors(values({ patient, type: 'Correction', corrections: [] })).details).toMatch(/List the changes/);
  });

  it('sends the changes only with a correction', () => {
    const corrections = [{ field: 'address' as const, requestedValue: ' 12 MG Road, Pune ' }];
    const correction = toCreateRequest(intakeSchema.parse(values({ patient, type: 'Correction', corrections })));
    expect(correction.corrections).toEqual([{ field: 'address', requestedValue: '12 MG Road, Pune' }]);
    expect(correction.details).toBeNull();
    const access = toCreateRequest(intakeSchema.parse(values({ patient, type: 'Access', details: 'x', corrections })));
    expect(access.corrections).toBeNull();
  });

  it('records an identity check done at the desk', () => {
    const base = values({ patient, type: 'Access', details: 'x', identityCheckedNow: true });
    expect(errors(base).identityCheck).toBe('Choose how you checked their identity.');
    expect(errors({ ...base, identityCheck: 'Other' }).identityNote).toBe('Say how the identity was checked.');
    const request = toCreateRequest(intakeSchema.parse({ ...base, identityCheck: 'PhotoId', identityNote: '' }));
    expect(request.identityCheck).toBe('PhotoId');
    expect(request.identityNote).toBeNull();
    // Unticking the box sends no check.
    expect(
      toCreateRequest(intakeSchema.parse({ ...base, identityCheckedNow: false, identityCheck: 'PhotoId' }))
        .identityCheck,
    ).toBeNull();
  });

  it('maps server validation keys onto the form', () => {
    expect(intakeFieldFor('patientId')).toBe('patient');
    expect(intakeFieldFor('contactPhone')).toBe('contactPhone');
    expect(intakeFieldFor('corrections[1].requestedValue')).toBe('corrections.1.requestedValue');
    expect(intakeFieldFor('corrections')).toBeNull();
    expect(intakeFieldFor('somethingElse')).toBeNull();
  });
});
