import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { DICTIONARIES } from '@/i18n/dictionary';
import {
  checkAddFamily,
  CODE_MINUTES,
  codeMinutes,
  emptyAddFamily,
  FAMILY_RELATIONS,
  familyErrorKey,
  isCodeSpent,
  phoneEnding,
  RELATION_KEY,
  toCodeRequest,
} from './model';

const refusal = (code: string) => new ApiError({ status: 422, title: 'Not allowed', code, detail: 'English text.' });

describe('adding a family member', () => {
  const valid = { uhid: ' nh26000124 ', phone: '+91 98111 22233', relation: 'Parent' as const };

  it('checks the UHID, the number and the relation before a code is spent', () => {
    expect(checkAddFamily(valid)).toEqual([]);
    expect(checkAddFamily(emptyAddFamily()).map((p) => p.path)).toEqual(['uhid', 'phone', 'relation']);
    expect(checkAddFamily({ ...valid, uhid: 'NH 2600' })).toEqual([{ path: 'uhid', key: 'family.add.uhid.invalid' }]);
    expect(checkAddFamily({ ...valid, uhid: 'N'.repeat(21) })).toEqual([
      { path: 'uhid', key: 'family.add.uhid.invalid' },
    ]);
    expect(checkAddFamily({ ...valid, phone: '12345 67890' })).toEqual([
      { path: 'phone', key: 'family.add.phone.invalid' },
    ]);
  });

  it('sends the UHID as printed and the number in full', () => {
    expect(toCodeRequest(valid)).toEqual({ uhid: 'NH26000124', phone: '+919811122233', relation: 'Parent' });
    expect(phoneEnding(valid.phone)).toBe('2233');
  });

  it('says how long the code works, even on a phone with a wrong clock', () => {
    const now = Date.parse('2026-10-07T09:00:00Z');
    expect(codeMinutes('2026-10-07T09:10:00Z', now)).toBe(10);
    expect(codeMinutes('2026-10-07T09:09:40Z', now)).toBe(10);
    expect(codeMinutes('2026-10-08T09:10:00Z', now)).toBe(CODE_MINUTES);
    expect(codeMinutes('2026-10-07T08:00:00Z', now)).toBe(CODE_MINUTES);
    expect(codeMinutes('not a date', now)).toBe(CODE_MINUTES);
  });

  it('puts the server’s refusals in the patient’s language', () => {
    expect(familyErrorKey(refusal('portal.family_code_throttled'))).toBe('family.error.throttled');
    expect(familyErrorKey(refusal('portal.family_code_expired'))).toBe('family.error.expired');
    expect(familyErrorKey(refusal('portal.family_already_linked'))).toBe('family.error.already');
    expect(familyErrorKey(refusal('portal.family_own_number'))).toBe('family.error.ownNumber');
    expect(
      familyErrorKey(new ApiError({ status: 400, title: 'Invalid', errors: { code: ['The code is incorrect.'] } })),
    ).toBe('family.error.wrongCode');
    expect(familyErrorKey(refusal('something.else'))).toBeNull();
    expect(familyErrorKey(new Error('offline'))).toBeNull();
    expect(isCodeSpent(refusal('portal.family_code_expired'))).toBe(true);
    expect(isCodeSpent(refusal('portal.family_code_throttled'))).toBe(false);
  });

  it('names every relation in both languages', () => {
    for (const relation of FAMILY_RELATIONS) {
      const key = RELATION_KEY[relation];
      expect(DICTIONARIES.en[key], key).toBeTruthy();
      expect(DICTIONARIES.hi[key], key).not.toBe(DICTIONARIES.en[key]);
    }
  });
});
