/**
 * Pure privacy rules mirrored from the server (DataRequest, Breach, RetentionSchedule) plus the words the screens use
 * for deadlines and clocks. The server stays the authority and checks everything again.
 */
import { formatDate, formatDateOnly, formatDateTime, HOSPITAL_TIME_ZONE } from '@hc360/core';
import { diffDays } from '@/modules/hr/shared/dates';
import type { DataRequestDetail, SummaryPerson } from './api';
import {
  correctableFieldLabel,
  CORRECTABLE_FIELDS,
  MAX_CORRECTIONS,
  type BreachStatus,
  type CorrectableField,
  type CorrectionDecision,
  type DataRequestEventKind,
  type DataRequestStatus,
  type DataRequestType,
  type RequestOutcome,
  type RetentionRunTrigger,
  type RetentionUnit,
} from './labels';

export type Tone = 'danger' | 'warning' | 'success' | 'brand' | 'informative';

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;

const istDateFormat = new Intl.DateTimeFormat('en-CA', {
  timeZone: HOSPITAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});
const dayMonthFormat = new Intl.DateTimeFormat('en-IN', {
  timeZone: HOSPITAL_TIME_ZONE,
  day: 'numeric',
  month: 'short',
});
const dayMonthYearFormat = new Intl.DateTimeFormat('en-IN', {
  timeZone: HOSPITAL_TIME_ZONE,
  day: 'numeric',
  month: 'short',
  year: 'numeric',
});

/** The hospital (IST) calendar date of an instant, "yyyy-MM-dd". */
export function istDate(ms: number): string {
  return istDateFormat.format(ms);
}

/** "12 Oct", with the year when it is not this year ("12 Oct 2027"). */
export function shortDate(ms: number, nowMs: number): string {
  return istDate(ms).slice(0, 4) === istDate(nowMs).slice(0, 4)
    ? dayMonthFormat.format(ms)
    : dayMonthYearFormat.format(ms);
}

function plural(count: number, word: string): string {
  return `${count} ${word}${count === 1 ? '' : 's'}`;
}

/** "a", "a and b", "a, b and c". */
export function listText(items: readonly string[]): string {
  if (items.length <= 1) {
    return items[0] ?? '';
  }
  return `${items.slice(0, -1).join(', ')} and ${items.at(-1)}`;
}

/** Time left, precisely: "20 min", "5 h 20 min", "2 days 3 h". */
export function durationText(ms: number): string {
  const totalMinutes = Math.max(0, Math.floor(ms / MINUTE));
  if (totalMinutes < 1) {
    return 'less than a minute';
  }
  if (totalMinutes < 60) {
    return `${totalMinutes} min`;
  }
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours < 24) {
    return minutes > 0 ? `${hours} h ${minutes} min` : `${hours} h`;
  }
  const days = Math.floor(hours / 24);
  const rest = hours % 24;
  return rest > 0 ? `${plural(days, 'day')} ${rest} h` : plural(days, 'day');
}

/** Time gone, roughly: "20 min", "3 h", "4 days". */
export function elapsedText(ms: number): string {
  const minutes = Math.max(1, Math.floor(ms / MINUTE));
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  return hours < 48 ? `${hours} h` : plural(Math.floor(hours / 24), 'day');
}

// ------------------------------------------------------------------ requests

export function isOpenStatus(status: DataRequestStatus): boolean {
  return status === 'Submitted' || status === 'InProgress';
}

/** Personal data is given out, changed or erased only for a checked requester; a grievance can be answered without. */
export function identityRequired(type: DataRequestType): boolean {
  return type !== 'Grievance';
}

export interface RequestFacts {
  type: DataRequestType;
  status: DataRequestStatus;
  identityVerified: boolean;
  corrections: readonly { decision: CorrectionDecision }[];
}

/** The facts the request rules need, from the request as the server sends it. */
export function requestFacts(r: DataRequestDetail): RequestFacts {
  return {
    type: r.type,
    status: r.status,
    identityVerified: r.identityVerifiedAt !== null,
    corrections: r.corrections,
  };
}

export function correctionProgress(corrections: readonly { decision: CorrectionDecision }[]): {
  total: number;
  pending: number;
  accepted: number;
  declined: number;
} {
  const accepted = corrections.filter((c) => c.decision === 'Accepted').length;
  const declined = corrections.filter((c) => c.decision === 'Declined').length;
  return { total: corrections.length, pending: corrections.length - accepted - declined, accepted, declined };
}

/** Why the request cannot be closed with this outcome yet (DataRequest.Close), or null when it can. */
export function outcomeBlocker(request: RequestFacts, outcome: RequestOutcome): string | null {
  if (!isOpenStatus(request.status)) {
    return 'This request is already closed.';
  }
  if (outcome === 'Declined') {
    return null;
  }
  if (identityRequired(request.type) && !request.identityVerified) {
    return 'Check the requester’s identity, and their authority to act for the patient, first. You can still decline the request.';
  }
  if (request.type === 'Correction') {
    const { pending } = correctionProgress(request.corrections);
    if (pending > 0) {
      return `Accept or decline every requested change first (${pending} still to decide). You can still decline the whole request.`;
    }
  }
  return null;
}

export interface RequestActions {
  start: boolean;
  verify: boolean;
  assign: boolean;
  note: boolean;
  answer: boolean;
}

/** The actions a request offers in its status to a user who may (or may not) manage requests. */
export function requestActions(request: RequestFacts, canManage: boolean): RequestActions {
  const open = isOpenStatus(request.status);
  return {
    start: canManage && request.status === 'Submitted',
    verify: canManage && open && !request.identityVerified,
    assign: canManage && open,
    // Staff notes can be added to closed requests too; they are never shown to the requester.
    note: canManage,
    answer: canManage && open,
  };
}

/** Why "Answer" can only decline for now (shown next to the button), or null when it can complete. */
export function answerHint(request: RequestFacts): string | null {
  if (!isOpenStatus(request.status)) {
    return null;
  }
  return outcomeBlocker(request, 'Completed');
}

/** A sensible outcome to start the answer with. */
export function suggestedOutcome(request: RequestFacts, erasure: { keptByLaw: boolean } | null = null): RequestOutcome {
  if (request.type === 'Correction') {
    const { pending, accepted, declined } = correctionProgress(request.corrections);
    if (pending === 0 && declined > 0) {
      return accepted === 0 ? 'Declined' : 'PartlyCompleted';
    }
    return 'Completed';
  }
  if (request.type === 'Erasure' && erasure?.keptByLaw) {
    return 'PartlyCompleted';
  }
  return 'Completed';
}

/** The text of the latest erasure recorded on the request ("Erased: …. Kept as the law requires: …"). */
export function latestErasureSummary(events: readonly { kind: DataRequestEventKind; text: string }[]): string | null {
  for (let index = events.length - 1; index >= 0; index--) {
    const event = events[index];
    if (event?.kind === 'Erased') {
      return event.text;
    }
  }
  return null;
}

/** A starting point for the reply; staff always read and edit it. Declining needs their own reason. */
export function suggestedReply(input: {
  type: DataRequestType;
  outcome: RequestOutcome;
  corrections: readonly { fieldLabel: string; decision: CorrectionDecision; note: string | null }[];
  erasureSummary: string | null;
}): string {
  if (input.outcome === 'Declined') {
    return '';
  }
  switch (input.type) {
    case 'Access':
      return (
        'Your data summary is ready. It lists the personal data we hold about you, why we hold it, how long we keep it ' +
        'and who we shared it with. You can see it in the patient portal, or ask us for a printed copy.'
      );
    case 'Correction': {
      const accepted = input.corrections.filter((c) => c.decision === 'Accepted').map((c) => c.fieldLabel);
      const declined = input.corrections
        .filter((c) => c.decision === 'Declined')
        .map((c) => (c.note ? `${c.fieldLabel} (${c.note})` : c.fieldLabel));
      const parts: string[] = [];
      if (accepted.length > 0) {
        parts.push(`We have made these changes: ${accepted.join('; ')}.`);
      }
      if (declined.length > 0) {
        parts.push(`We did not make these changes: ${declined.join('; ')}.`);
      }
      return parts.join(' ');
    }
    case 'Erasure':
      return input.erasureSummary ?? '';
    default:
      return '';
  }
}

export interface DeadlineText {
  text: string;
  overdue: boolean;
  /** Three days or less to go. */
  soon: boolean;
}

/** "Reply by 12 Oct (in 3 days)", "Reply by 12 Oct (today)", "Overdue by 2 days", "Overdue by 3 h". */
export function replyByText(dueAtIso: string, nowMs: number): DeadlineText {
  const due = Date.parse(dueAtIso);
  if (!Number.isFinite(due)) {
    return { text: '—', overdue: false, soon: false };
  }
  if (due >= nowMs) {
    const days = diffDays(istDate(nowMs), istDate(due));
    const when = days <= 0 ? 'today' : days === 1 ? 'tomorrow' : `in ${days} days`;
    return { text: `Reply by ${shortDate(due, nowMs)} (${when})`, overdue: false, soon: days <= 3 };
  }
  const days = diffDays(istDate(due), istDate(nowMs));
  return {
    text: days <= 0 ? `Overdue by ${elapsedText(nowMs - due)}` : `Overdue by ${plural(days, 'day')}`,
    overdue: true,
    soon: false,
  };
}

/** "Closed 12 Oct" (with the year when it is not this year). */
export function closedText(closedAtIso: string | null | undefined, nowMs: number): string | null {
  const closed = closedAtIso ? Date.parse(closedAtIso) : Number.NaN;
  return Number.isFinite(closed) ? `Closed ${shortDate(closed, nowMs)}` : null;
}

export function requestStatusTone(status: DataRequestStatus, overdue: boolean): Tone {
  if (overdue && isOpenStatus(status)) {
    return 'danger';
  }
  switch (status) {
    case 'Submitted':
      return 'warning';
    case 'InProgress':
      return 'brand';
    case 'Completed':
    case 'PartlyCompleted':
      return 'success';
    default:
      return 'informative';
  }
}

// ------------------------------------------------------------------ correction intake

/** The details still free to choose for one row of the correction list (each detail once). */
export function availableCorrectionFields(chosen: readonly string[], current: string): CorrectableField[] {
  return CORRECTABLE_FIELDS.filter((field) => field === current || !chosen.includes(field));
}

export function canAddCorrection(count: number): boolean {
  return count < MAX_CORRECTIONS && count < CORRECTABLE_FIELDS.length;
}

/** Where a correction is made: registration details in the patient record, the rest by the clinical team or by hand. */
export function correctionKind(field: string): 'registration' | 'health' | 'other' {
  if (field === 'healthRecord') {
    return 'health';
  }
  return field in correctableFieldLabel && field !== 'other' ? 'registration' : 'other';
}

// ------------------------------------------------------------------ breaches

/** Less time than this left on the 72-hour clock is urgent. */
const URGENT_MS = 12 * HOUR;

export type ClockState = 'running' | 'urgent' | 'overdue' | 'sent' | 'closed';

export interface BreachClock {
  state: ClockState;
  text: string;
}

/** The Board's 72-hour clock: "Report due in 5 h 20 min", "Report overdue by 3 h", "Report sent on …". */
export function breachClock(
  breach: { status: BreachStatus; reportDueAt: string; reportSubmittedAt: string | null },
  nowMs: number,
): BreachClock {
  if (breach.reportSubmittedAt) {
    return { state: 'sent', text: `Report sent on ${formatDateTime(breach.reportSubmittedAt)}` };
  }
  if (breach.status === 'Closed') {
    return { state: 'closed', text: 'Closed without a report to the Board' };
  }
  const left = Date.parse(breach.reportDueAt) - nowMs;
  if (left >= 0) {
    return { state: left < URGENT_MS ? 'urgent' : 'running', text: `Report due in ${durationText(left)}` };
  }
  return { state: 'overdue', text: `Report overdue by ${elapsedText(-left)}` };
}

/** The clock for the register's list, which knows the status but not when the report was sent. */
/** The clock for the register's list, in fewer words: "Report sent 3 Oct", "Report due in 5 h 20 min". */
export function breachListClock(
  breach: { status: BreachStatus; reportDueAt: string; reportSubmittedAt: string | null },
  nowMs: number,
): BreachClock {
  if (breach.reportSubmittedAt) {
    return { state: 'sent', text: `Report sent ${shortDate(Date.parse(breach.reportSubmittedAt), nowMs)}` };
  }
  if (breach.status === 'ReportSubmitted') {
    return { state: 'sent', text: 'Report sent' };
  }
  if (breach.status === 'Closed') {
    return { state: 'closed', text: 'Closed without a report' };
  }
  return breachClock(breach, nowMs);
}

export function clockTone(state: ClockState): Tone {
  switch (state) {
    case 'overdue':
      return 'danger';
    case 'urgent':
      return 'warning';
    case 'sent':
      return 'success';
    case 'closed':
      return 'informative';
    default:
      return 'brand';
  }
}

export function breachStatusTone(status: BreachStatus, reportOverdue: boolean): Tone {
  if (reportOverdue) {
    return 'danger';
  }
  switch (status) {
    case 'Open':
      return 'warning';
    case 'BoardInformed':
      return 'brand';
    case 'ReportSubmitted':
      return 'success';
    default:
      return 'informative';
  }
}

export interface StepState {
  visible: boolean;
  /** Why the step cannot be taken yet (shown on the disabled button). */
  blocker: string | null;
}

export interface BreachFacts {
  status: BreachStatus;
  missingForReport: readonly string[];
  missingForNotice: readonly string[];
  affectedListed: number;
  notified: number;
}

export interface BreachSteps {
  boardInformed: StepState;
  extension: StepState;
  reportSubmitted: StepState;
  close: StepState & { noteRequired: boolean };
  edit: StepState;
  notify: StepState & { pending: number };
  addPeople: StepState;
}

/** The breach register's steps (Breach.Record…, Close, BreachService.Notify) and why each is not possible yet. */
export function breachSteps(breach: BreachFacts): BreachSteps {
  const open = breach.status === 'Open';
  const informed = breach.status === 'BoardInformed';
  const closed = breach.status === 'Closed';
  const pending = Math.max(0, breach.affectedListed - breach.notified);
  let reportBlocker: string | null = null;
  if (open) {
    reportBlocker = 'Record when the Board was first told before recording the detailed report.';
  } else if (breach.missingForReport.length > 0) {
    reportBlocker = `Fill in ${listText(breach.missingForReport)} first.`;
  }
  let notifyBlocker: string | null = null;
  if (breach.missingForNotice.length > 0) {
    notifyBlocker = `Fill in ${listText(breach.missingForNotice)} first.`;
  } else if (breach.affectedListed === 0) {
    notifyBlocker = 'Add the people affected first.';
  } else if (pending === 0) {
    notifyBlocker = 'Everyone on the list has been sent the notice.';
  }
  return {
    boardInformed: { visible: open, blocker: null },
    extension: { visible: open || informed, blocker: null },
    reportSubmitted: { visible: open || informed, blocker: reportBlocker },
    close: { visible: !closed, blocker: null, noteRequired: breach.status !== 'ReportSubmitted' },
    edit: { visible: !closed, blocker: null },
    notify: { visible: !closed, blocker: notifyBlocker, pending },
    addPeople: { visible: true, blocker: closed ? 'A closed breach cannot be changed.' : null },
  };
}

/** The step that moves the breach on, shown as the primary button. */
export function nextBreachStep(status: BreachStatus): 'boardInformed' | 'reportSubmitted' | 'close' | null {
  switch (status) {
    case 'Open':
      return 'boardInformed';
    case 'BoardInformed':
      return 'reportSubmitted';
    case 'ReportSubmitted':
      return 'close';
    default:
      return null;
  }
}

/** The earliest end of an extension: the Board can only extend beyond the usual 72 hours. */
export function extensionMinimum(awareAtIso: string): string {
  return new Date(Date.parse(awareAtIso) + 72 * HOUR).toISOString();
}

/** "120 listed · 118 told", or the estimate while nobody is listed. */
export function breachPeopleText(breach: {
  estimatedAffected: number | null;
  affectedListed: number;
  notified: number;
}): string {
  if (breach.affectedListed === 0) {
    return breach.estimatedAffected !== null
      ? `About ${breach.estimatedAffected.toLocaleString('en-IN')}, not listed yet`
      : 'Nobody listed yet';
  }
  return `${breach.affectedListed.toLocaleString('en-IN')} listed · ${breach.notified.toLocaleString('en-IN')} told`;
}

/** "sms,email" → "SMS and e-mail"; "none" → "No contact details". */
export function noticeChannelsText(channels: string | null | undefined): string {
  if (!channels) {
    return '';
  }
  if (channels === 'none') {
    return 'No contact details';
  }
  const words = channels
    .split(',')
    .map((c) => c.trim().toLowerCase())
    .filter(Boolean)
    .map((c) => (c === 'sms' ? 'SMS' : c === 'email' ? 'e-mail' : c === 'whatsapp' ? 'WhatsApp' : c));
  const text = listText(words);
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** UHIDs pasted one per line or separated by commas, semicolons or spaces: trimmed, upper-cased, each once. */
export function parseUhids(text: string): string[] {
  const seen = new Set<string>();
  for (const part of text.split(/[\s,;]+/)) {
    const uhid = part.trim().toUpperCase();
    if (uhid) {
      seen.add(uhid);
    }
  }
  return [...seen];
}

/** The last four digits of a masked phone number ("+91******3210" → "3210"). */
export function phoneTail(masked: string | null | undefined): string | null {
  const digits = (masked ?? '').replace(/\D/g, '');
  return digits.length >= 4 ? digits.slice(-4) : null;
}

// ------------------------------------------------------------------ retention

function unitWord(unit: RetentionUnit, value = 2): string {
  const word = unit === 'Years' ? 'year' : 'day';
  return value === 1 ? word : `${word}s`;
}

/** "30 days", "365 days (1 year)", "5 years", "Permanently". */
export function retentionPeriodText(value: number | null, unit: RetentionUnit): string {
  if (value === null) {
    return 'Permanently';
  }
  if (unit === 'Years') {
    return plural(value, 'year');
  }
  // A year of days is 365, or 366 so that a leap day never shortens it (the logs' period).
  const years = value % 365 === 0 ? value / 365 : value % 366 === 0 ? value / 366 : 0;
  if (years > 0) {
    return `${value} days (${plural(years, 'year')})`;
  }
  return plural(value, 'day');
}

export interface RetentionRuleFacts {
  key: string;
  unit: RetentionUnit;
  defaultValue: number | null;
  minimumValue: number | null;
  maximumValue: number;
  editable: boolean;
}

/** The allowed range for a period (RetentionService.UpdateAsync). */
export function retentionRange(rule: RetentionRuleFacts): { min: number; max: number; text: string } {
  const min = rule.minimumValue ?? 1;
  const max = rule.maximumValue;
  return { min, max, text: `Between ${min} and ${max} ${unitWord(rule.unit)}` };
}

export function retentionValueError(rule: RetentionRuleFacts, value: number | null): string | null {
  if (value === null || !Number.isInteger(value)) {
    return 'Enter a whole number.';
  }
  const { min, max } = retentionRange(rule);
  return value < min || value > max ? `Choose between ${min} and ${max} ${unitWord(rule.unit)}.` : null;
}

/** Why a period cannot be changed here, or null when it can. */
export function retentionFixedReason(rule: RetentionRuleFacts): string | null {
  if (rule.editable && rule.defaultValue !== null) {
    return null;
  }
  if (rule.defaultValue === null) {
    return 'Kept permanently. This cannot be changed.';
  }
  if (rule.key === 'backups') {
    return 'Set in the server’s backup settings. Ask IT to change it.';
  }
  return 'Fixed by law or hospital policy, so it cannot be changed here.';
}

/** "Nightly", or who started it: "Started by Meera Iyer". */
export function retentionRunSource(trigger: RetentionRunTrigger, startedByName: string | null): string {
  if (trigger === 'Scheduled') {
    return 'Nightly';
  }
  return startedByName ? `Started by ${startedByName}` : 'Started by an administrator';
}

export function isMedicalRecordsRule(key: string): boolean {
  return key === 'records.outpatient' || key === 'records.inpatient';
}

/** "Until 12 Mar 2031" or "Permanently". */
export function keptUntilText(until: string | null, permanently: boolean): string {
  if (permanently) {
    return 'Permanently';
  }
  return until ? `Until ${formatDateOnly(until)}` : '—';
}

/**
 * The date of birth as held, marked when it was worked out from the age given at registration: the patient reads it in
 * the summary, and a day and month she never gave are not her birthday.
 */
export function birthDateText(person: Pick<SummaryPerson, 'dateOfBirth' | 'isDateOfBirthEstimated'>): string | null {
  if (!person.dateOfBirth) {
    return null;
  }
  const date = formatDateOnly(person.dateOfBirth);
  return person.isDateOfBirthEstimated ? `${date} (estimated from the age given at registration)` : date;
}

/** "3 Jan 2021 – 12 Sept 2026", or one date when both are the same day. */
export function itemDatesText(first: string | null, last: string | null): string {
  if (!first && !last) {
    return '';
  }
  const from = formatDate(first ?? last);
  const to = formatDate(last ?? first);
  return from === to ? from : `${from} – ${to}`;
}

// ------------------------------------------------------------------ officers, nominees, links

/**
 * The people a request can be given to: the signed-in officer first ("(you)"), then the others by name. The current
 * owner stays in the list even if they no longer hold the officer's access, so the choice shown is never blank.
 */
export function officerChoices(
  officers: readonly { id: string; displayName: string }[],
  meId: string | null | undefined,
  current: { id: string; name: string } | null,
): { id: string; label: string }[] {
  const choices = officers
    .map((o) => ({ id: o.id, label: o.id === meId ? `${o.displayName} (you)` : o.displayName }))
    .sort((a, b) => (a.id === meId ? -1 : b.id === meId ? 1 : a.label.localeCompare(b.label)));
  if (current && !choices.some((c) => c.id === current.id)) {
    choices.push({ id: current.id, label: `${current.name} (current owner)` });
  }
  return choices;
}

/** How the nominee was recorded, in words. */
export function nomineeSourceText(recordedVia: string, updatedAtIso: string): string {
  const when = formatDate(updatedAtIso);
  switch (recordedVia) {
    case 'Portal':
      return `Named by the patient in the patient portal, ${when}`;
    case 'FrontDesk':
      return `Recorded at the front desk, ${when}`;
    default:
      return `Recorded ${when}`;
  }
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A record id as the server writes it; anything else in a link is ignored. */
export function isRecordId(value: unknown): value is string {
  return typeof value === 'string' && GUID.test(value);
}
