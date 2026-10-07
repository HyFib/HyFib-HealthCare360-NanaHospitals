import { ApiError } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type { PortalFamilyLinkDto } from './api';
import {
  endedText,
  linkSourceText,
  ownNumberText,
  shareFormDefaults,
  shareFormSchema,
  shareServerErrors,
  toLinkRequest,
  type ShareFormValues,
} from './portalAccess';

function link(patch: Partial<PortalFamilyLinkDto>): PortalFamilyLinkDto {
  return {
    id: 'l1',
    accountPhone: '+919811122233',
    relation: 'Parent',
    source: 'FrontDesk',
    linkedAt: '2026-10-01T05:00:00Z',
    linkedByName: 'Asha Nair',
    consentGivenBy: 'the patient',
    codeSentTo: null,
    revokedAt: null,
    revokedByName: null,
    revokedReason: null,
    isActive: true,
    ...patch,
  };
}

function messages(values: Partial<ShareFormValues>, ownPhone: string | null = '+919800000001') {
  const result = shareFormSchema(ownPhone).safeParse({ ...shareFormDefaults(), ...values });
  return result.success ? {} : Object.fromEntries(result.error.issues.map((i) => [i.path.join('.'), i.message]));
}

describe('the share form', () => {
  const valid: Partial<ShareFormValues> = { accountPhone: '98111 22233', relation: 'Parent', identityChecked: true };

  it('accepts a family member’s number once identities are checked', () => {
    expect(messages(valid)).toEqual({});
    expect(messages({ ...valid, identityChecked: false })).toEqual({
      identityChecked: 'Check who the patient and the family member are before sharing the records.',
    });
  });

  it('refuses the patient’s own number and a number that is not an Indian mobile', () => {
    expect(messages({ ...valid, accountPhone: '+91 98000 00001' }).accountPhone).toBe(
      'This is the patient’s own number: its portal account sees the records already.',
    );
    expect(messages({ ...valid, accountPhone: '12345' }).accountPhone).toBe(
      'Enter the family member’s 10-digit mobile number.',
    );
    expect(messages({ ...valid, accountPhone: '9800000001' }, null)).toEqual({});
  });

  it('needs the relation, and a guardian’s name when a guardian agreed', () => {
    expect(messages({ ...valid, relation: null }).relation).toBe('Choose how the patient is related to them.');
    expect(messages({ ...valid, agreedBy: 'guardian' }).guardianName).toBe(
      'Enter the name of the guardian who agreed.',
    );
  });

  it('sends the number in full international form and who agreed in words', () => {
    expect(toLinkRequest({ ...shareFormDefaults(), ...valid } as ShareFormValues)).toEqual({
      accountPhone: '+919811122233',
      relation: 'Parent',
      consentGivenBy: 'the patient',
      identityChecked: true,
    });
    expect(
      toLinkRequest({
        ...shareFormDefaults(),
        ...valid,
        agreedBy: 'guardian',
        guardianName: ' Ravi Kumar ',
      } as ShareFormValues).consentGivenBy,
    ).toBe('Ravi Kumar, guardian');
  });

  it('puts validation messages on the fields the form shows and the rest above the buttons', () => {
    const errors = shareServerErrors(
      new ApiError({
        status: 400,
        title: 'Please correct the highlighted fields.',
        errors: { accountPhone: ['Enter the family member’s 10-digit mobile number.'], consentGivenBy: ['Too long.'] },
      }),
    );
    expect(errors).toEqual({
      fields: [['accountPhone', 'Enter the family member’s 10-digit mobile number.']],
      other: ['Too long.'],
    });
    expect(
      shareServerErrors(
        new ApiError({
          status: 409,
          title: 'Conflict',
          code: 'portal.family_already_linked',
          detail: 'This mobile number can already see the record.',
        }),
      ),
    ).toEqual({ fields: [], other: ['This mobile number can already see the record.'] });
  });
});

describe('the words for a link', () => {
  it('says who sees the records through the patient’s own number', () => {
    expect(ownNumberText({ ownPhone: '+919800000001', links: [] })).toBe(
      'Anyone who signs in to the patient portal with the patient’s own number, +91 98000 00001, sees these records.',
    );
    expect(ownNumberText({ ownPhone: null, links: [] })).toBe(
      'The patient has no mobile number, so no portal account sees these records through its own number.',
    );
  });

  it('says how the link was made and when it ended', () => {
    expect(linkSourceText(link({}))).toBe('At the front desk, agreed by the patient');
    expect(linkSourceText(link({ source: 'PortalCode', consentGivenBy: null, codeSentTo: '+919800000001' }))).toBe(
      'In the portal, with a code sent to +91 98000 00001',
    );
    expect(endedText(link({}))).toBeNull();
    expect(endedText(link({ revokedAt: '2026-10-02T05:00:00Z', revokedByName: 'Asha Nair' }))).toMatch(
      /^Ended .+ by Asha Nair$/,
    );
  });
});
