import { describe, expect, it } from 'vitest';
import { CONSENT_TYPES, type ConsentDto } from './api';
import {
  CONSENT_GROUPS,
  consentStatus,
  consentStatusBadge,
  consentTypeHint,
  consentTypeLabel,
  givenSummary,
  isStandingPermission,
  otherGivenConsents,
  otherGivenNote,
  recordedNotice,
  signedByText,
  withdrawalEffect,
  withdrawnNotice,
  withdrawnText,
} from './consents';

const when = (iso: string) => `<${iso}>`;

function consent(patch: Partial<ConsentDto> = {}): ConsentDto {
  return {
    id: 'c1',
    type: 'DataSharing',
    consentVersion: '1.0',
    granted: true,
    capturedAt: '2026-09-20T05:00:00Z',
    capturedByName: 'Asha Nair',
    witnessName: null,
    signedByRelation: null,
    signedByName: null,
    documentFileId: null,
    revokedAt: null,
    revokedReason: null,
    revokedByName: null,
    notes: null,
    isActive: true,
    ...patch,
  };
}

describe('consent types', () => {
  it('names every type in plain words', () => {
    expect(consentTypeLabel('DataSharing')).toBe('Sharing records with other care providers');
    expect(consentTypeLabel('SmsWhatsAppUpdates')).toBe('SMS and WhatsApp updates');
    expect(consentTypeLabel('Telehealth')).toBe('Video consultations');
    for (const type of CONSENT_TYPES) {
      expect(consentTypeLabel(type, () => 'missing')).not.toBe('missing');
    }
  });

  it('humanises a type it does not know yet, and ignores object built-ins', () => {
    expect(consentTypeLabel('OrganDonation')).toBe('Organ donation');
    expect(consentTypeLabel('constructor', (value) => `[${value}]`)).toBe('[constructor]');
  });

  it('offers every type exactly once in the record dialog', () => {
    const offered = CONSENT_GROUPS.flatMap((group) => group.types);
    expect([...offered].sort()).toEqual([...CONSENT_TYPES].sort());
  });

  it('explains what a consent covers only where its name does not', () => {
    expect(consentTypeHint('SmsWhatsAppUpdates')).toBe(
      'Covers feedback requests and follow-up reminders, not appointment or report messages.',
    );
    expect(consentTypeHint('HighRiskProcedure')).toBe('Name the procedure in the notes.');
    expect(consentTypeHint('Research')).toBeUndefined();
    expect(consentTypeHint(null)).toBeUndefined();
  });
});

describe('a consent in the list', () => {
  it('is given, refused or withdrawn', () => {
    expect(consentStatus(consent())).toBe('given');
    expect(consentStatus(consent({ granted: false, isActive: false }))).toBe('refused');
    expect(consentStatus(consent({ revokedAt: '2026-10-01T05:00:00Z', isActive: false }))).toBe('withdrawn');
    expect(consentStatusBadge.withdrawn).toEqual({ status: 'Revoked', label: 'Withdrawn' });
  });

  it('says who signed: the patient, or the person signing on their behalf', () => {
    expect(signedByText(consent())).toBe('Patient');
    expect(signedByText(consent({ signedByName: ' Ravi Kumar ', signedByRelation: 'son' }))).toBe('Ravi Kumar (son)');
    expect(signedByText(consent({ signedByName: 'Ravi Kumar' }))).toBe('Ravi Kumar');
    expect(signedByText(consent({ signedByName: '  ', signedByRelation: 'father' }))).toBe('father');
  });

  it('identifies the consent about to be withdrawn', () => {
    expect(givenSummary(consent(), when)).toBe('Given on <2026-09-20T05:00:00Z>, signed by the patient.');
    expect(givenSummary(consent({ signedByName: 'Meena Devi', signedByRelation: 'mother' }), when)).toBe(
      'Given on <2026-09-20T05:00:00Z>, signed by Meena Devi (mother).',
    );
  });

  it('says when and by whom it was withdrawn', () => {
    expect(withdrawnText(consent(), when)).toBeNull();
    expect(withdrawnText(consent({ revokedAt: '2026-10-01T05:00:00Z', revokedByName: 'Asha Nair' }), when)).toBe(
      '<2026-10-01T05:00:00Z> by Asha Nair',
    );
    // A STOP reply on WhatsApp is withdrawn by no member of staff.
    expect(withdrawnText(consent({ revokedAt: '2026-10-01T05:00:00Z' }), when)).toBe('<2026-10-01T05:00:00Z>');
  });
});

describe('withdrawing a consent', () => {
  it('claims only what HC360 does for consents it acts on', () => {
    expect(withdrawalEffect('DataSharing').effect).toBe(
      'HC360 stops sharing this patient’s records with other care providers straight away.',
    );
    expect(withdrawalEffect('SmsWhatsAppUpdates').effect).toContain(
      'Appointment and report messages are not covered by this consent.',
    );
    expect(withdrawalEffect('MarketingCommunication').next).toBe(
      'The patient is left out of health-offer campaigns from now on.',
    );
  });

  it('tells staff what to do for consents HC360 only records', () => {
    expect(withdrawalEffect('Telehealth').next).toBe('Record a new consent before the next video consultation.');
    expect(withdrawalEffect('HighRiskProcedure').effect).toBe(
      'Tell the treating doctor. The procedure must not be done without a new consent.',
    );
    for (const type of ['GeneralTreatment', 'HighRiskProcedure', 'Anaesthesia', 'BloodTransfusion']) {
      expect(withdrawalEffect(type).next).toMatch(/^Tell the treating doctor now\./);
    }
    expect(withdrawalEffect('OrganDonation')).toEqual({
      effect: 'HC360 records the withdrawal.',
      next: 'The withdrawal is recorded.',
    });
  });

  it('finds the other consents of the same type that still stand', () => {
    const list = [
      consent({ id: 'new' }),
      consent({ id: 'old', capturedAt: '2025-03-03T05:00:00Z' }),
      consent({ id: 'gone', revokedAt: '2025-06-01T05:00:00Z', isActive: false }),
      consent({ id: 'refusal', granted: false, isActive: false }),
      consent({ id: 'sms', type: 'SmsWhatsAppUpdates' }),
    ];
    expect(otherGivenConsents(list, { id: 'new', type: 'DataSharing' }).map((c) => c.id)).toEqual(['old']);
    expect(otherGivenConsents(list, { id: 'sms', type: 'SmsWhatsAppUpdates' })).toEqual([]);
  });

  it('withdraws a standing permission as a whole, and a procedure consent on its own', () => {
    expect(isStandingPermission('DataSharing')).toBe(true);
    expect(isStandingPermission('Photography')).toBe(true);
    expect(isStandingPermission('HighRiskProcedure')).toBe(false);
    expect(isStandingPermission('GeneralTreatment')).toBe(false);

    const older = [{ capturedAt: '2025-03-03T05:00:00Z' }];
    expect(otherGivenNote('DataSharing', [], when)).toBeNull();
    expect(otherGivenNote('DataSharing', older, when)).toBe(
      'The other consent for “Sharing records with other care providers”, given on <2025-03-03T05:00:00Z>, is withdrawn too.',
    );
    expect(otherGivenNote('Research', [...older, ...older], when)).toBe(
      'The 2 other consents for “Research” are withdrawn too.',
    );
    expect(otherGivenNote('HighRiskProcedure', older, when)).toBe(
      'Only this consent is withdrawn. The other consent for “High-risk procedure”, given on <2025-03-03T05:00:00Z>, stays.',
    );
    expect(withdrawnNotice('DataSharing')).toEqual({
      title: 'Consent withdrawn',
      body: 'This patient’s records are no longer shared with other care providers.',
    });
  });
});

describe('recording a consent', () => {
  it('says what changes where HC360 acts on the consent', () => {
    expect(recordedNotice('DataSharing', true)).toEqual({
      title: 'Consent recorded',
      body: 'HC360 can now share this patient’s records with other care providers.',
    });
    expect(recordedNotice('SmsWhatsAppUpdates', false)).toEqual({
      title: 'Refusal recorded',
      body: 'Feedback requests and follow-up reminders are not sent to the patient.',
    });
    expect(recordedNotice('Anaesthesia', false).body).toBe('Tell the treating doctor.');
  });

  it('otherwise names the consent', () => {
    expect(recordedNotice('Telehealth', true)).toEqual({ title: 'Consent recorded', body: 'Video consultations' });
    expect(recordedNotice('Anaesthesia', true).body).toBe('Anaesthesia');
  });
});
