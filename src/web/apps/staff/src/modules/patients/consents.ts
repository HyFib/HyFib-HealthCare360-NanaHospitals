/**
 * Patient consents (EHR-06) as the screens describe them (pure, unit-tested): the plain words for each consent type,
 * a consent's status and who signed it, and what recording or withdrawing one changes. The server stays the authority
 * for every rule; these helpers only describe what it does.
 */
import { formatDate, formatDateTime, humanize } from '@hc360/core';
import type { ConsentDto, ConsentType, WhatsAppConsentDto, WhatsAppOptInSource } from './api';

const CONSENT_LABELS: Record<ConsentType, string> = {
  GeneralTreatment: 'General treatment',
  DataSharing: 'Sharing records with other care providers',
  Telehealth: 'Video consultations',
  Research: 'Research',
  SmsWhatsAppUpdates: 'SMS and WhatsApp updates',
  MarketingCommunication: 'Health offers and news',
  Photography: 'Photographs',
  HighRiskProcedure: 'High-risk procedure',
  Anaesthesia: 'Anaesthesia',
  BloodTransfusion: 'Blood transfusion',
  PrivacyNotice: 'Privacy notice given',
};

function isKnownType(type: string): type is ConsentType {
  return Object.hasOwn(CONSENT_LABELS, type);
}

/** A consent type in plain words (the privacy screens use them too); a type added later shows its humanised name. */
export function consentTypeLabel(type: string, fallback: (value: string) => string = humanize): string {
  return isKnownType(type) ? CONSENT_LABELS[type] : fallback(type);
}

/** The record dialog's choices, grouped so related consents sit together. */
export const CONSENT_GROUPS: readonly { label: string; types: readonly ConsentType[] }[] = [
  { label: 'Treatment', types: ['GeneralTreatment', 'HighRiskProcedure', 'Anaesthesia', 'BloodTransfusion'] },
  {
    label: 'Records and messages',
    types: ['PrivacyNotice', 'DataSharing', 'SmsWhatsAppUpdates', 'MarketingCommunication'],
  },
  { label: 'Other', types: ['Telehealth', 'Photography', 'Research'] },
];

const TYPE_HINTS: Partial<Record<ConsentType, string>> = {
  PrivacyNotice: 'Records that the patient was given the hospital’s privacy notice, at the version now in force.',
  DataSharing: 'HC360 shares the patient’s records with other care providers only while this consent is given.',
  SmsWhatsAppUpdates: 'Covers feedback requests and follow-up reminders, not appointment or report messages.',
  MarketingCommunication: 'Covers health-offer campaigns.',
  HighRiskProcedure: 'Name the procedure in the notes.',
  Anaesthesia: 'Name the procedure in the notes.',
  BloodTransfusion: 'Say in the notes what the transfusion is for.',
};

/** What the chosen consent covers or needs, where its name does not say it. */
export function consentTypeHint(type: string | null): string | undefined {
  return type && isKnownType(type) ? TYPE_HINTS[type] : undefined;
}

export type ConsentStatus = 'given' | 'refused' | 'withdrawn';

/** Given, refused, or given and later withdrawn. */
export function consentStatus(consent: Pick<ConsentDto, 'granted' | 'revokedAt'>): ConsentStatus {
  return consent.revokedAt ? 'withdrawn' : consent.granted ? 'given' : 'refused';
}

/** For `StatusBadge`: the status word sets the colour, the label is what staff read. */
export const consentStatusBadge: Record<ConsentStatus, { status: string; label: string }> = {
  given: { status: 'Approved', label: 'Given' },
  refused: { status: 'Rejected', label: 'Refused' },
  withdrawn: { status: 'Revoked', label: 'Withdrawn' },
};

/** Who signed on the patient's behalf ("Ravi Kumar (son)"), or null when the patient signed. */
export function proxySigner(consent: Pick<ConsentDto, 'signedByName' | 'signedByRelation'>): string | null {
  const name = consent.signedByName?.trim();
  const relation = consent.signedByRelation?.trim();
  if (name) {
    return relation ? `${name} (${relation})` : name;
  }
  return relation || null;
}

/** The "Signed by" column: "Patient", or who signed on the patient's behalf. */
export function signedByText(consent: Pick<ConsentDto, 'signedByName' | 'signedByRelation'>): string {
  return proxySigner(consent) ?? 'Patient';
}

/** "Given on 2 Oct 2026, 10:30 am, signed by the patient." — tells apart the consent about to be withdrawn. */
export function givenSummary(
  consent: Pick<ConsentDto, 'capturedAt' | 'signedByName' | 'signedByRelation'>,
  formatWhen: (iso: string) => string = formatDateTime,
): string {
  return `Given on ${formatWhen(consent.capturedAt)}, signed by ${proxySigner(consent) ?? 'the patient'}.`;
}

/** When, and by whom, a consent was withdrawn ("2 Oct 2026, 11:05 am by Asha Nair"); null while it stands. */
export function withdrawnText(
  consent: Pick<ConsentDto, 'revokedAt' | 'revokedByName'>,
  formatWhen: (iso: string) => string = formatDateTime,
): string | null {
  if (!consent.revokedAt) {
    return null;
  }
  const by = consent.revokedByName?.trim();
  return by ? `${formatWhen(consent.revokedAt)} by ${by}` : formatWhen(consent.revokedAt);
}

export interface WithdrawalEffect {
  /** What withdrawing changes, said before it is done. */
  effect: string;
  /** What happens next, said once it is done. */
  next: string;
}

// Only what HC360 actually does: sharing, campaigns, feedback requests and follow-up reminders check consent; for the
// other consents the withdrawal is a record that staff act on.
const WITHDRAWAL: Record<ConsentType, WithdrawalEffect> = {
  DataSharing: {
    effect: 'HC360 stops sharing this patient’s records with other care providers straight away.',
    next: 'This patient’s records are no longer shared with other care providers.',
  },
  SmsWhatsAppUpdates: {
    effect:
      'Feedback requests and follow-up reminders stop straight away. Appointment and report messages are not covered by this consent.',
    next: 'Feedback requests and follow-up reminders have stopped.',
  },
  MarketingCommunication: {
    effect: 'The patient is left out of health-offer campaigns from now on.',
    next: 'The patient is left out of health-offer campaigns from now on.',
  },
  Telehealth: {
    effect: 'HC360 records the withdrawal. Video consultations need a new consent first.',
    next: 'Record a new consent before the next video consultation.',
  },
  Research: {
    effect: 'HC360 records the withdrawal. The patient’s information must not be used for research from now on.',
    next: 'Stop using the patient’s information for research.',
  },
  Photography: {
    effect: 'HC360 records the withdrawal. Photographs of the patient must not be taken or used from now on.',
    next: 'Do not take or use photographs of the patient without a new consent.',
  },
  GeneralTreatment: {
    effect: 'Tell the treating doctor. Treatment must not continue without a new consent.',
    next: 'Tell the treating doctor now. Treatment must not continue without a new consent.',
  },
  HighRiskProcedure: {
    effect: 'Tell the treating doctor. The procedure must not be done without a new consent.',
    next: 'Tell the treating doctor now. The procedure must not be done without a new consent.',
  },
  Anaesthesia: {
    effect: 'Tell the treating doctor. Anaesthesia must not be given without a new consent.',
    next: 'Tell the treating doctor now. Anaesthesia must not be given without a new consent.',
  },
  BloodTransfusion: {
    effect: 'Tell the treating doctor. Blood must not be transfused without a new consent.',
    next: 'Tell the treating doctor now. Blood must not be transfused without a new consent.',
  },
  PrivacyNotice: {
    effect: 'HC360 records that the acknowledgement was withdrawn. Give the patient the notice again.',
    next: 'Give the patient the privacy notice again.',
  },
};

const UNKNOWN_WITHDRAWAL: WithdrawalEffect = {
  effect: 'HC360 records the withdrawal.',
  next: 'The withdrawal is recorded.',
};

export function withdrawalEffect(type: string): WithdrawalEffect {
  return isKnownType(type) ? WITHDRAWAL[type] : UNKNOWN_WITHDRAWAL;
}

// The server's PatientConsent.IsStandingPermission: permissions that hold until withdrawn, so withdrawing one, or
// recording a refusal, ends every grant of the type. Treatment and procedure consents belong to one stay or operation.
const STANDING_PERMISSIONS: readonly ConsentType[] = [
  'DataSharing',
  'SmsWhatsAppUpdates',
  'MarketingCommunication',
  'Telehealth',
  'Research',
  'Photography',
  'PrivacyNotice',
];

export function isStandingPermission(type: string): boolean {
  return (STANDING_PERMISSIONS as readonly string[]).includes(type);
}

/**
 * The patient's other consents of the same type that are still given. For a standing permission the withdrawal ends
 * them too; a consent for one stay or procedure is withdrawn on its own.
 */
export function otherGivenConsents(
  consents: readonly ConsentDto[],
  consent: Pick<ConsentDto, 'id' | 'type'>,
): ConsentDto[] {
  return consents.filter((c) => c.id !== consent.id && c.type === consent.type && c.isActive);
}

/** What the withdrawal does to the patient's other given consents of the type; null when there are none. */
export function otherGivenNote(
  type: string,
  others: readonly Pick<ConsentDto, 'capturedAt'>[],
  formatWhen: (iso: string) => string = formatDate,
): string | null {
  const [only] = others;
  if (!only) {
    return null;
  }
  const label = consentTypeLabel(type);
  if (isStandingPermission(type)) {
    return others.length === 1
      ? `The other consent for “${label}”, given on ${formatWhen(only.capturedAt)}, is withdrawn too.`
      : `The ${others.length} other consents for “${label}” are withdrawn too.`;
  }
  return others.length === 1
    ? `Only this consent is withdrawn. The other consent for “${label}”, given on ${formatWhen(only.capturedAt)}, stays.`
    : `Only this consent is withdrawn. The ${others.length} other consents for “${label}” stay.`;
}

export interface ConsentNotice {
  title: string;
  body: string;
}

/** The toast once a consent is withdrawn: what happens next. */
export function withdrawnNotice(type: string): ConsentNotice {
  return { title: 'Consent withdrawn', body: withdrawalEffect(type).next };
}

const GIVEN_NEXT: Partial<Record<ConsentType, string>> = {
  DataSharing: 'HC360 can now share this patient’s records with other care providers.',
  SmsWhatsAppUpdates: 'Feedback requests and follow-up reminders can now be sent to the patient.',
  MarketingCommunication: 'The patient can now be included in health-offer campaigns.',
};

const REFUSED_NEXT: Partial<Record<ConsentType, string>> = {
  DataSharing: 'HC360 does not share this patient’s records with other care providers.',
  SmsWhatsAppUpdates: 'Feedback requests and follow-up reminders are not sent to the patient.',
  MarketingCommunication: 'The patient is left out of health-offer campaigns.',
  GeneralTreatment: 'Tell the treating doctor.',
  HighRiskProcedure: 'Tell the treating doctor.',
  Anaesthesia: 'Tell the treating doctor.',
  BloodTransfusion: 'Tell the treating doctor.',
};

/** The toast once a consent is recorded: what changes where HC360 acts on it, else which consent it was. */
export function recordedNotice(type: string, granted: boolean): ConsentNotice {
  const next = isKnownType(type) ? (granted ? GIVEN_NEXT : REFUSED_NEXT)[type] : undefined;
  return { title: granted ? 'Consent recorded' : 'Refusal recorded', body: next ?? consentTypeLabel(type) };
}

const OPT_IN_SOURCES: Record<WhatsAppOptInSource, string> = {
  RegistrationDesk: 'at the desk',
  Portal: 'in the patient portal',
  WhatsAppStart: 'by replying START on WhatsApp',
};

export interface WhatsAppSummary {
  intent: 'success' | 'warning' | 'info';
  title: string;
  body: string;
  /** Recording the opt-in at the desk would change something (and there is a number to record it for). */
  canRecord: boolean;
}

/**
 * The WhatsApp choices of the patient's number in plain words (INT-WA). A STOP outranks every opt-in, and only the
 * number's holder can lift it (START, or the portal), so the desk does not record over it.
 */
export function whatsAppSummary(
  status: WhatsAppConsentDto,
  formatWhen: (iso: string) => string = formatDateTime,
): WhatsAppSummary {
  if (!status.phone) {
    return {
      intent: 'info',
      title: 'No mobile number',
      body: 'Add a mobile number to the registration before recording WhatsApp choices.',
      canRecord: false,
    };
  }
  if (status.optedOut) {
    const since = status.optedOutAt ? `Since ${formatWhen(status.optedOutAt)} it` : 'It';
    return {
      intent: 'warning',
      title: `${status.phone} replied STOP on WhatsApp`,
      body:
        `${since} gets only sign-in codes on WhatsApp: no bills, reports or reminders. ` +
        'Only the number’s holder can change that, by replying START or switching WhatsApp on in the patient portal.',
      canRecord: false,
    };
  }
  if (status.optedIn) {
    const by = status.capturedByName ? ` by ${status.capturedByName}` : '';
    const where = status.source ? ` ${OPT_IN_SOURCES[status.source]}` : '';
    const when = status.optedInAt ? ` on ${formatWhen(status.optedInAt)}` : '';
    return {
      intent: 'success',
      title: `${status.phone} gets WhatsApp reminders and updates`,
      body: `Opted in${where}${when}${by}.`,
      canRecord: false,
    };
  }
  return {
    intent: 'info',
    title: `${status.phone} has not opted in to WhatsApp reminders`,
    body: 'Bills and reports still go on WhatsApp. Reminders, feedback requests and offers need the patient’s agreement: ask them, and record it here.',
    canRecord: true,
  };
}
