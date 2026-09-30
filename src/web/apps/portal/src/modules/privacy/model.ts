import { isApiError } from '@hc360/core';
import type { Tone } from '@/components/ListRow';
import type { MessageKey, Translate } from '@/i18n';
import { parseCalendarDate } from '@/i18n/format';
import type { Formatters } from '@/i18n/useFormat';
import { isValidMobile, sanitizePhoneInput, toE164 } from '@/lib/otp';
import { GENDER_KEY } from '@/lib/people';
import {
  CORRECTABLE_FIELDS,
  type CorrectableField,
  type DataRequestStatus,
  type NomineeDto,
  type PortalBreachNotice,
  type PortalDataRequestCreate,
  type PortalDataRequestDetail,
  type PortalNomineeRequest,
  type PortalProfile,
  type PortalRequestEvent,
  type PortalRequester,
  type RetentionPeriods,
  type SummaryCategory,
  type SummaryConsent,
  type SummaryItem,
  type SummaryPerson,
} from '@/lib/types';
import {
  BREACH_DATA,
  CATEGORY,
  CONSENT_TYPE,
  ERASED_ITEM,
  EVENT_KIND,
  FIELD_FORM,
  FIELD_LABEL,
  ITEM_LABEL,
  KIND_TYPE,
  REQUEST_KINDS,
  REQUESTER,
  REQUESTER_NAMED,
  type RequestKind,
} from './labels';

/** The server's limits (PortalDataRequestCreateValidator, CorrectionListValidator, NomineeRules). */
export const VALUE_MAX = 500;
export const DETAILS_MAX = 4000;
export const NAME_MAX = 200;
export const RELATIONSHIP_MAX = 64;
export const EMAIL_MAX = 200;
/** The genders a correction can ask for ("Unknown" is what the hospital records when it does not know). */
export const GENDER_CHOICES = ['Male', 'Female', 'Other'] as const;

/** Looks a server-sent key up in a map without ever reading inherited properties ("constructor", "toString"). */
function own<T>(map: Readonly<Record<string, T>>, key: string): T | undefined {
  return Object.hasOwn(map, key) ? map[key] : undefined;
}

// ------------------------------------------------------------------ requests

/** `?type=` of the new-request page; anything else is dropped (the page then asks about a concern). */
export function parseRequestKind(value: unknown): RequestKind | undefined {
  return typeof value === 'string' && (REQUEST_KINDS as readonly string[]).includes(value)
    ? (value as RequestKind)
    : undefined;
}

/** Waiting for the hospital: received, or being worked on. */
export function isOpenRequest(status: DataRequestStatus): boolean {
  return status === 'Submitted' || status === 'InProgress';
}

/** An open request whose reply date has passed. */
export function isReplyLate(item: { status: DataRequestStatus; dueAt: string }, nowMs: number): boolean {
  const due = Date.parse(item.dueAt);
  return isOpenRequest(item.status) && Number.isFinite(due) && due < nowMs;
}

/** 422 `privacy.too_many_open`: the patient already has the most open requests the portal allows. */
export function isTooManyOpen(error: unknown): boolean {
  return isApiError(error) && error.code === 'privacy.too_many_open';
}

/**
 * A line of the request's history in the patient's language. The reply and what was erased are shown in full above
 * the history, so their lines repeat the server's words only when there is something the page does not already say.
 */
export function eventView(
  event: PortalRequestEvent,
  shown: { response: string | null | undefined; erasureShown: boolean },
  t: Translate,
): { label: string; text: string | null } {
  const key = own(EVENT_KIND, event.kind);
  const text = event.text?.trim() || null;
  if (!key) {
    return { label: text ?? event.kind, text: null };
  }
  if (event.kind === 'Erased') {
    return { label: t(key), text: shown.erasureShown ? null : text };
  }
  if (event.kind === 'Closed') {
    return { label: t(key), text: text && text !== (shown.response ?? '').trim() ? text : null };
  }
  return { label: t(key), text: null };
}

/** Who asked: "The patient", or "{name}, as a family member" when someone else asked and gave their name. */
export function requesterText(
  request: Pick<PortalDataRequestDetail, 'requestedBy' | 'requesterName'>,
  t: Translate,
): string {
  const name = request.requesterName?.trim();
  const named = request.requestedBy === 'Self' ? undefined : own(REQUESTER_NAMED, request.requestedBy);
  if (named && name) {
    return t(named, { name });
  }
  const key = own(REQUESTER, request.requestedBy);
  return key ? t(key) : request.requestedBy;
}

/**
 * An open request made for someone else, whose requester the hospital has not yet confirmed may act for the patient:
 * nothing is changed or erased until then. A concern needs no such check (the hospital answers it either way).
 */
export function awaitsRequesterCheck(
  request: Pick<PortalDataRequestDetail, 'type' | 'status' | 'requestedBy' | 'identityConfirmed'>,
): boolean {
  return (
    isOpenRequest(request.status) &&
    request.requestedBy !== 'Self' &&
    !request.identityConfirmed &&
    request.type !== 'Grievance'
  );
}

/** What an erasure removed, in the patient's words, and how long the law keeps the rest; null when nothing to say. */
export function erasureOutcome(
  request: Pick<PortalDataRequestDetail, 'type' | 'retainedUntil' | 'retainedPermanently'> & {
    erasedItems: readonly string[];
  },
  t: Translate,
  f: Pick<Formatters, 'calendarDate'>,
): { erased: string[]; kept: string | null } | null {
  if (request.type !== 'Erasure') {
    return null;
  }
  const erased = (request.erasedItems ?? []).map((item) => {
    const key = own(ERASED_ITEM as Readonly<Record<string, MessageKey>>, item);
    return key ? t(key) : item;
  });
  let kept: string | null = null;
  if (request.retainedPermanently) {
    kept = t('myPrivacy.request.keptPermanently');
  } else if (request.retainedUntil) {
    kept = t('myPrivacy.request.keptUntil', { date: f.calendarDate(request.retainedUntil) });
  }
  return erased.length > 0 || kept ? { erased, kept } : null;
}

export function fieldLabel(field: string, t: Translate): string {
  const key = own(FIELD_LABEL as Readonly<Record<string, MessageKey>>, field);
  return key ? t(key) : field;
}

/** A value in a correction (on record, or asked for) as the patient reads it: genders and dates in their language. */
export function correctionValue(field: string, value: string, t: Translate, f: Formatters): string {
  const plain = value.trim();
  if (field === 'gender') {
    const key = own(GENDER_KEY as Readonly<Record<string, MessageKey>>, plain);
    return key ? t(key) : plain;
  }
  if (field === 'dateOfBirth' && parseCalendarDate(plain)) {
    return f.calendarDate(plain);
  }
  if (field === 'mobile' || field === 'alternatePhone') {
    return formatPhone(plain) ?? plain;
  }
  return plain;
}

// ------------------------------------------------------------------ the request form

export interface RequestFormValues {
  requestedBy: PortalRequester | null;
  requesterName: string;
  /** The details chosen for correction. */
  fields: CorrectableField[];
  /** The correct value of each chosen detail. */
  values: Record<CorrectableField, string>;
  details: string;
  /** Erasure: "I understand what the law requires the hospital to keep". */
  understood: boolean;
}

export function emptyRequestForm(): RequestFormValues {
  return {
    requestedBy: null,
    requesterName: '',
    fields: [],
    values: Object.fromEntries(CORRECTABLE_FIELDS.map((field) => [field, ''])) as Record<CorrectableField, string>,
    details: '',
    understood: false,
  };
}

export interface FormProblem {
  /** The form path, as segments ("values", "name"). */
  path: string[];
  key: MessageKey;
}

const EMAIL = /^[^\s@<>()[\]\\,;:"'?&#%/]+@[^\s@<>()[\]\\,;:"'?&#%/]+\.[^\s@<>()[\]\\,;:"'?&#%/]+$/;

export function isEmail(value: string): boolean {
  return EMAIL.test(value.trim());
}

/** A phone number staff can dial: digits with an optional "+", spaces, brackets or dashes; 6 to 15 digits. */
export function isPhoneNumber(value: string): boolean {
  const digits = value.replace(/\D/g, '').length;
  return /^\+?[\d\s()-]+$/.test(value.trim()) && digits >= 6 && digits <= 15;
}

/** A real calendar date ("yyyy-MM-dd") from 1900 up to today. */
export function isPastDate(value: string, today: string): boolean {
  return parseCalendarDate(value) !== null && value >= '1900-01-01' && value <= today;
}

/** What is wrong with the correct value typed for `field`, or null when it can be sent. */
export function valueProblem(field: CorrectableField, raw: string, today: string): MessageKey | null {
  const value = raw.trim();
  const input = FIELD_FORM[field].input;
  if (!value) {
    return input === 'gender' ? 'myPrivacy.new.value.choose' : 'myPrivacy.new.value.required';
  }
  if (value.length > VALUE_MAX) {
    return 'myPrivacy.new.value.tooLong';
  }
  switch (input) {
    case 'mobile':
      return isValidMobile(sanitizePhoneInput(value)) ? null : 'myPrivacy.new.value.mobile';
    case 'phone':
      return isPhoneNumber(value) ? null : 'myPrivacy.new.value.phone';
    case 'email':
      return isEmail(value) ? null : 'myPrivacy.new.value.email';
    case 'date':
      return isPastDate(value, today) ? null : 'myPrivacy.new.value.date';
    case 'gender':
      return (GENDER_CHOICES as readonly string[]).includes(value) ? null : 'myPrivacy.new.value.choose';
    default:
      return null;
  }
}

/** Every problem with the form before it is sent (the server applies the same rules). */
export function checkRequest(values: RequestFormValues, kind: RequestKind, today: string): FormProblem[] {
  const problems: FormProblem[] = [];
  if (!values.requestedBy) {
    problems.push({ path: ['requestedBy'], key: 'myPrivacy.new.who.required' });
  } else if (values.requestedBy !== 'Self') {
    const name = values.requesterName.trim();
    if (!name) {
      problems.push({ path: ['requesterName'], key: 'myPrivacy.new.yourName.required' });
    } else if (name.length > NAME_MAX) {
      problems.push({ path: ['requesterName'], key: 'myPrivacy.name.tooLong' });
    }
  }
  if (kind === 'correction') {
    if (values.fields.length === 0) {
      problems.push({ path: ['fields'], key: 'myPrivacy.new.fields.required' });
    }
    for (const field of CORRECTABLE_FIELDS) {
      if (values.fields.includes(field)) {
        const key = valueProblem(field, values.values[field] ?? '', today);
        if (key) {
          problems.push({ path: ['values', field], key });
        }
      }
    }
  }
  const details = values.details.trim();
  if (details.length > DETAILS_MAX) {
    problems.push({ path: ['details'], key: 'myPrivacy.new.details.tooLong' });
  } else if (kind !== 'correction' && !details) {
    problems.push({ path: ['details'], key: 'myPrivacy.new.details.required' });
  }
  if (kind === 'erasure' && !values.understood) {
    problems.push({ path: ['understood'], key: 'myPrivacy.new.understood.required' });
  }
  return problems;
}

/** The details sent for correction, in the order the request lists them (server errors refer to that order). */
export function sentFields(values: Pick<RequestFormValues, 'fields'>, kind: RequestKind): CorrectableField[] {
  return kind === 'correction' ? CORRECTABLE_FIELDS.filter((field) => values.fields.includes(field)) : [];
}

function normalizedValue(field: CorrectableField, raw: string): string {
  return FIELD_FORM[field].input === 'mobile' ? toE164(sanitizePhoneInput(raw)) : raw.trim();
}

/** The request body. Only a correction lists changes; a request for yourself carries no requester name. */
export function buildRequest(
  values: RequestFormValues,
  kind: RequestKind,
  patientId: string,
  requestedBy: PortalRequester,
): PortalDataRequestCreate {
  const corrections = sentFields(values, kind).map((field) => ({
    field,
    requestedValue: normalizedValue(field, values.values[field] ?? ''),
  }));
  return {
    patientId,
    type: KIND_TYPE[kind],
    requestedBy,
    requesterName: requestedBy === 'Self' ? null : values.requesterName.trim() || null,
    details: values.details.trim() || null,
    corrections: corrections.length > 0 ? corrections : null,
  };
}

/** Where a server validation error belongs on the request form (`corrections[1].requestedValue` → the 2nd detail sent). */
export function requestFormPath(serverField: string, sent: readonly CorrectableField[]): string | null {
  if (serverField === 'requestedBy' || serverField === 'requesterName' || serverField === 'details') {
    return serverField;
  }
  if (serverField === 'corrections') {
    return 'fields';
  }
  const match = /^corrections\[(\d+)\]/.exec(serverField);
  if (match) {
    const field = sent[Number(match[1])];
    return field ? `values.${field}` : 'fields';
  }
  return null;
}

export interface MappedServerErrors {
  fields: { path: string; message: string }[];
  /** Messages for fields the form does not show (said in a notice instead, never lost). */
  other: string[];
}

/** A 400 with field errors, split into the fields the form shows and the rest; null for any other failure. */
export function mapServerErrors(error: unknown, toPath: (field: string) => string | null): MappedServerErrors | null {
  if (!isApiError(error) || !error.isValidation) {
    return null;
  }
  const mapped: MappedServerErrors = { fields: [], other: [] };
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    const message = messages[0];
    if (!message) {
      continue;
    }
    const path = toPath(field);
    if (path) {
      mapped.fields.push({ path, message });
    } else {
      mapped.other.push(message);
    }
  }
  return mapped;
}

// ------------------------------------------------------------------ the nominee form

export interface NomineeFormValues {
  name: string;
  relationship: string;
  /** Ten national digits (the input keeps nothing else). */
  phone: string;
  email: string;
}

export const NOMINEE_FIELDS = ['name', 'relationship', 'phone', 'email'] as const;

export function nomineeFormValues(nominee: NomineeDto | null | undefined): NomineeFormValues {
  return {
    name: nominee?.name ?? '',
    relationship: nominee?.relationship ?? '',
    phone: nominee?.phone ? sanitizePhoneInput(nominee.phone) : '',
    email: nominee?.email ?? '',
  };
}

export function checkNominee(values: NomineeFormValues): { path: keyof NomineeFormValues; key: MessageKey }[] {
  const problems: { path: keyof NomineeFormValues; key: MessageKey }[] = [];
  const name = values.name.trim();
  if (!name) {
    problems.push({ path: 'name', key: 'myPrivacy.nominee.name.required' });
  } else if (name.length > NAME_MAX) {
    problems.push({ path: 'name', key: 'myPrivacy.name.tooLong' });
  }
  const relationship = values.relationship.trim();
  if (!relationship) {
    problems.push({ path: 'relationship', key: 'myPrivacy.nominee.relationship.required' });
  } else if (relationship.length > RELATIONSHIP_MAX) {
    problems.push({ path: 'relationship', key: 'myPrivacy.nominee.relationship.tooLong' });
  }
  const phone = sanitizePhoneInput(values.phone);
  const email = values.email.trim();
  if (!phone && !email) {
    problems.push({ path: 'phone', key: 'myPrivacy.nominee.contact.required' });
  } else if (phone && !isValidMobile(phone)) {
    problems.push({ path: 'phone', key: 'myPrivacy.new.value.mobile' });
  }
  if (email && (email.length > EMAIL_MAX || !isEmail(email))) {
    problems.push({ path: 'email', key: 'myPrivacy.new.value.email' });
  }
  return problems;
}

export function buildNomineeRequest(values: NomineeFormValues, patientId: string): PortalNomineeRequest {
  const phone = sanitizePhoneInput(values.phone);
  return {
    patientId,
    name: values.name.trim(),
    relationship: values.relationship.trim(),
    phone: phone ? toE164(phone) : null,
    email: values.email.trim() || null,
  };
}

// ------------------------------------------------------------------ contact details

/** "+91 98765 43210" for an Indian mobile number; any other number as given. */
export function formatPhone(phone: string | null | undefined): string | null {
  const value = phone?.trim();
  if (!value) {
    return null;
  }
  const match = /^\+91(\d{5})(\d{5})$/.exec(value);
  return match ? `+91 ${match[1]} ${match[2]}` : value;
}

/** A tel: link for a number that looks dialable; null otherwise (the number is then shown as plain text). */
export function telHref(phone: string | null | undefined): string | null {
  const cleaned = (phone ?? '').trim().replace(/[\s()-]/g, '');
  if (!/^\+?\d{6,15}$/.test(cleaned)) {
    return null;
  }
  return `tel:${cleaned}`;
}

/** A mailto: link for a plain e-mail address; null otherwise (never a link that carries a subject or body). */
export function mailtoHref(email: string | null | undefined): string | null {
  const value = email?.trim();
  return value && isEmail(value) ? `mailto:${value}` : null;
}

/** The contact's role in the patient's language when it is one of the roles the law names. */
export function contactTitle(title: string | null | undefined, t: Translate): string {
  const plain = (title ?? '').trim();
  if (!plain || /^grievance officer$/i.test(plain)) {
    return t('myPrivacy.contact.officer');
  }
  return /^data protection officer$/i.test(plain) ? t('myPrivacy.contact.dpo') : plain;
}

// ------------------------------------------------------------------ the data summary

/**
 * "How long we keep it" in the patient's language, built from the group's kind of retention and the periods in force
 * (the server's `periods`). A kind the portal does not know yet keeps the server's English sentence.
 */
export function retentionText(
  category: Pick<SummaryCategory, 'retention' | 'retentionKind'>,
  periods: RetentionPeriods | null | undefined,
  t: Translate,
  f: Pick<Formatters, 'number'>,
): string {
  const kind = category.retentionKind;
  if (kind === 'WhileInUse') {
    return t('myPrivacy.retention.whileInUse');
  }
  if (kind === 'WithRecords') {
    return t('myPrivacy.retention.withRecords');
  }
  if (periods) {
    switch (kind) {
      case 'MedicalRecords':
        return t('myPrivacy.retention.medical', {
          outpatient: f.number(periods.outpatientYears),
          inpatient: f.number(periods.inpatientYears),
        });
      case 'Finance':
        return t('myPrivacy.retention.finance', { years: f.number(periods.financeYears) });
      case 'MessageLog':
        return t('myPrivacy.retention.messages', { days: f.number(periods.messageLogDays) });
      case 'Audit':
        return t('myPrivacy.retention.years', { years: f.number(periods.auditYears) });
    }
  }
  return category.retention;
}

export interface ItemView {
  label: string;
  count: string;
  dates: string | null;
}

/** One kind of record held: its label in the patient's language (by table; else as the server names it), how many, when. */
export function itemView(item: SummaryItem, t: Translate, f: Formatters): ItemView {
  const key = item.key ? own(ITEM_LABEL, item.key) : undefined;
  const first = item.first ? f.date(item.first) : null;
  const last = item.last ? f.date(item.last) : null;
  let dates: string | null = null;
  if (first && last && first !== last) {
    dates = t('myPrivacy.data.item.range', { first, last });
  } else if (first || last) {
    dates = t('myPrivacy.data.item.on', { date: (first ?? last) as string });
  }
  return {
    label: key ? t(key) : item.label,
    count: t('myPrivacy.data.item.count', { count: f.number(item.count) }),
    dates,
  };
}

export interface CategoryView {
  key: string;
  title: string;
  description: string;
  purpose: string;
  basis: string;
  retention: string;
  items: ItemView[];
}

/** A group of the summary in the patient's language, by its stable key; an unknown group keeps the server's text. */
export function categoryView(
  category: SummaryCategory,
  periods: RetentionPeriods | null | undefined,
  t: Translate,
  f: Formatters,
): CategoryView {
  const keys = own(CATEGORY, category.key);
  return {
    key: category.key,
    title: keys ? t(keys.title) : category.title,
    description: keys ? t(keys.description) : category.description,
    purpose: keys ? t(keys.purpose) : category.purpose,
    basis: keys ? t(keys.basis) : category.basis,
    retention: retentionText(category, periods, t, f),
    items: category.items.map((item) => itemView(item, t, f)),
  };
}

/** The registration group (always sent) goes with the registration details; the rest are shown in order. */
export function splitCategories(categories: readonly SummaryCategory[]): {
  registration: SummaryCategory | null;
  others: SummaryCategory[];
} {
  return {
    registration: categories.find((category) => category.key === 'registration') ?? null,
    others: categories.filter((category) => category.key !== 'registration'),
  };
}

export interface ConsentView {
  label: string;
  state: MessageKey;
  tone: Tone;
  detail: string;
}

export function consentView(consent: SummaryConsent, t: Translate, f: Formatters): ConsentView {
  const key = own(CONSENT_TYPE, consent.type);
  const label = key ? t(key) : consent.type;
  if (!consent.granted) {
    return {
      label,
      state: 'myPrivacy.consent.refused',
      tone: 'neutral',
      detail: t('myPrivacy.consent.refusedOn', { date: f.date(consent.capturedAt) }),
    };
  }
  if (consent.revokedAt) {
    return {
      label,
      state: 'myPrivacy.consent.withdrawn',
      tone: 'neutral',
      detail: t('myPrivacy.consent.withdrawnOn', {
        given: f.date(consent.capturedAt),
        date: f.date(consent.revokedAt),
      }),
    };
  }
  return {
    label,
    state: 'myPrivacy.consent.given',
    tone: 'success',
    detail: t('myPrivacy.consent.givenOn', { date: f.date(consent.capturedAt) }),
  };
}

/** The registration details held, in the patient's language; details not held are left out. */
export function personRows(person: SummaryPerson, t: Translate, f: Formatters): { label: string; value: string }[] {
  const genderKey = own(GENDER_KEY as Readonly<Record<string, MessageKey>>, person.gender);
  const rows: { label: string; value: string | null }[] = [
    { label: t('common.uhid'), value: person.uhid },
    { label: t('myPrivacy.field.name'), value: person.fullName },
    {
      label: t('myPrivacy.field.gender'),
      value: person.gender === 'Unknown' ? null : genderKey ? t(genderKey) : person.gender,
    },
    {
      label: t('myPrivacy.field.dateOfBirth'),
      value: person.dateOfBirth ? f.calendarDate(person.dateOfBirth) : null,
    },
    { label: t('myPrivacy.field.mobile'), value: formatPhone(person.phone) },
    { label: t('myPrivacy.field.alternatePhone'), value: formatPhone(person.alternatePhone) },
    { label: t('myPrivacy.field.email'), value: person.email },
    { label: t('myPrivacy.field.address'), value: person.address },
    { label: t('myPrivacy.field.emergencyContact'), value: person.emergencyContact },
    // National identifiers exactly as the hospital stores them: masked.
    { label: t('myPrivacy.data.person.abha'), value: person.abhaNumber },
    { label: t('myPrivacy.data.person.abhaAddress'), value: person.abhaAddress },
    { label: t('myPrivacy.data.person.aadhaar'), value: person.aadhaarLast4 },
    {
      label: t('myPrivacy.data.person.photo'),
      value: t(person.hasPhoto ? 'myPrivacy.data.person.photo.yes' : 'myPrivacy.data.person.photo.no'),
    },
    { label: t('myPrivacy.data.person.registered'), value: f.date(person.registeredAt) },
    { label: t('myPrivacy.data.person.facility'), value: person.registeredAtFacility },
  ];
  return rows.flatMap((row) => (row.value?.trim() ? [{ label: row.label, value: row.value.trim() }] : []));
}

// ------------------------------------------------------------------ breach notices

/**
 * Whose notice this is, for `OwnerGuard`: the person being viewed when it concerns them, otherwise a family member it
 * concerns (one on the profile list first, so the page can name them). A notice that names nobody is treated as the
 * viewer's, as before the server said whom each notice concerns.
 */
export function noticeOwner(
  patientIds: readonly string[] | null | undefined,
  activeId: string,
  profiles: readonly Pick<PortalProfile, 'id'>[],
): string {
  const ids = patientIds ?? [];
  if (ids.length === 0 || ids.includes(activeId)) {
    return activeId;
  }
  return ids.find((id) => profiles.some((profile) => profile.id === id)) ?? ids[0] ?? activeId;
}

/** When the incident happened (a period or a moment) and when the hospital found out. */
export function noticeWhen(
  notice: Pick<PortalBreachNotice, 'occurredFrom' | 'occurredTo' | 'awareAt'>,
  t: Translate,
  f: Formatters,
): string[] {
  const lines: string[] = [];
  const { occurredFrom: from, occurredTo: to } = notice;
  if (from) {
    lines.push(
      to && to !== from
        ? t('myPrivacy.notice.between', { from: f.dateTime(from), to: f.dateTime(to) })
        : t('myPrivacy.notice.on', { date: f.dateTime(from) }),
    );
  }
  lines.push(t('myPrivacy.notice.found', { date: f.dateTime(notice.awareAt) }));
  return lines;
}

export function noticeData(categories: readonly string[], t: Translate): string[] {
  return categories.map((category) => {
    const key = own(BREACH_DATA as Readonly<Record<string, MessageKey>>, category);
    return key ? t(key) : category;
  });
}
