import { describe, expect, it } from 'vitest';
import { nomineeDefaults, nomineeSchema, toSaveNominee, type NomineeValues } from './nominee';

function errors(v: NomineeValues): Record<string, string> {
  const result = nomineeSchema.safeParse(v);
  return result.success
    ? {}
    : Object.fromEntries(result.error.issues.map((issue) => [issue.path.join('.'), issue.message]));
}

describe('nominee form', () => {
  it('needs a name, the relationship and a way to reach them', () => {
    const e = errors(nomineeDefaults(null));
    expect(e.name).toBe('Enter the nominee’s name.');
    expect(e.relationship).toBe('Say how the nominee is related to the patient.');
    expect(e.phone).toMatch(/mobile number or e-mail address/);
  });

  it('checks the mobile number and e-mail address', () => {
    const base = { name: 'Meena Rao', relationship: 'Sister', phone: '', email: '' };
    expect(errors({ ...base, phone: '12345' }).phone).toBe('Enter a valid 10-digit mobile number.');
    // A landline is not a mobile number.
    expect(errors({ ...base, phone: '020 2612 3456' }).phone).toBe('Enter a valid 10-digit mobile number.');
    expect(errors({ ...base, email: 'meena@' }).email).toBe('Enter a valid e-mail address.');
    expect(errors({ ...base, email: 'meena@example.com' })).toEqual({});
    expect(errors({ ...base, phone: '98765 43210' })).toEqual({});
  });

  it('sends the mobile number in the server’s form and leaves empty answers out', () => {
    expect(toSaveNominee({ name: ' Meena Rao ', relationship: ' Sister ', phone: '098765 43210', email: ' ' })).toEqual(
      { name: 'Meena Rao', relationship: 'Sister', phone: '+919876543210', email: null },
    );
  });

  it('replaces the nominee shown, at the version shown (REL-04)', () => {
    const values = { name: 'Meena Rao', relationship: 'Sister', phone: '9876543210', email: '' };
    expect(toSaveNominee(values, 7).version).toBe(7);
    expect(toSaveNominee(values).version).toBeUndefined();
  });

  it('starts from the nominee on record when replacing', () => {
    expect(
      nomineeDefaults({
        name: 'Meena Rao',
        relationship: 'Sister',
        phone: '+919876543210',
        email: null,
        recordedVia: 'Portal',
        updatedAt: '2026-09-01T05:00:00Z',
        version: 7,
      }),
    ).toEqual({ name: 'Meena Rao', relationship: 'Sister', phone: '+91 98765 43210', email: '' });
  });
});
