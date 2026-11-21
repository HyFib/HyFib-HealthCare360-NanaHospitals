import { formatDateOnly, formatDateTime, formatInr, formatNumber, isApiError } from '@hc360/core';
import {
  givenBySurgeon,
  type AnaesthesiaType,
  type ChecklistItemDto,
  type ConsentOnRecord,
  type OtCalendarBooking,
  type OtStoreBatch,
  type OtStoreItem,
  type SafetyPhase,
  type SchedulingConflict,
  type SurgeryDetail,
  type SurgeryPriority,
  type SurgeryStatus,
  type SurgicalConsentCheck,
  type SurgicalConsentDto,
  type SurgicalConsentKind,
} from './api';
import { phaseLabel, sideShort } from './labels';
import { minutesIntoIstDay } from './shared/datetime';

/**
 * Pure OT rules for the screens. They mirror the server (`SurgeryBooking`, `SurgeryScheduling`) so buttons are
 * disabled with the same explanation the server would give; the server still enforces every rule.
 */

export interface Gate {
  allowed: boolean;
  reason: string | null;
}

const ALLOWED: Gate = { allowed: true, reason: null };
const refuse = (reason: string): Gate => ({ allowed: false, reason });

/** The fields of a booking the gates look at (a `SurgeryDetail` satisfies it). */
export type GateInput = Pick<SurgeryDetail, 'status' | 'preOpComplete' | 'safetyChecklist' | 'consent'>;

export function isPhaseComplete(detail: Pick<SurgeryDetail, 'safetyChecklist'>, phase: SafetyPhase): boolean {
  return detail.safetyChecklist.some((p) => p.phase === phase && p.isComplete);
}

/** Statuses that hold a theatre slot (a completed case no longer blocks it). */
export const BLOCKING_STATUSES: readonly SurgeryStatus[] = ['Scheduled', 'InPreOp', 'InProgress'];

const DAY_OF_SURGERY: readonly SurgeryStatus[] = ['Scheduled', 'InPreOp'];

export function prerequisiteOf(phase: SafetyPhase): SafetyPhase | null {
  return phase === 'TimeOut' ? 'SignIn' : phase === 'SignOut' ? 'TimeOut' : null;
}

export interface PhaseGate extends Gate {
  /** The phase is complete: its items can no longer change. */
  locked: boolean;
}

/**
 * Whether items of a WHO phase can be confirmed now — same order of checks as `SurgeryBooking.ConfirmSafety`:
 * locked when complete, phases in order, Sign-in/Time-out on the day of surgery, Sign-out only during surgery,
 * the Sign-in only after a complete pre-operative checklist, and the Sign-in and Time-out only with the patient's consent
 * on record (or waived in an emergency).
 */
export function phaseGate(detail: GateInput, phase: SafetyPhase): PhaseGate {
  if (isPhaseComplete(detail, phase)) {
    return { allowed: false, locked: true, reason: `${phaseLabel[phase]} is complete and locked.` };
  }
  if (detail.status === 'Cancelled') {
    return { allowed: false, locked: false, reason: 'This booking is cancelled.' };
  }
  const before = prerequisiteOf(phase);
  if (before && !isPhaseComplete(detail, before)) {
    return { allowed: false, locked: false, reason: `Complete the ${phaseLabel[before]} first.` };
  }
  if (phase === 'SignOut') {
    if (detail.status !== 'InProgress') {
      return {
        allowed: false,
        locked: false,
        reason: 'The Sign-out is done at the end of surgery, after it has started.',
      };
    }
  } else if (!DAY_OF_SURGERY.includes(detail.status)) {
    const reason =
      detail.status === 'Requested' || detail.status === 'Postponed'
        ? 'Schedule the surgery first. The checklist is done on the day of surgery.'
        : `The ${phaseLabel[phase]} is done on the day of surgery for a scheduled patient.`;
    return { allowed: false, locked: false, reason };
  }
  if (phase === 'SignIn' && !detail.preOpComplete) {
    return { allowed: false, locked: false, reason: 'Complete the pre-operative checklist first.' };
  }
  if (phase !== 'SignOut') {
    const consented = consentGate(detail);
    if (!consented.allowed) {
      return { ...consented, locked: false };
    }
  }
  return { allowed: true, locked: false, reason: null };
}

/** Pre-operative checklist: editable until the WHO Sign-in, while the case has not started or closed. */
export function preOpGate(detail: GateInput): Gate {
  if (isPhaseComplete(detail, 'SignIn')) {
    return refuse('The pre-operative checklist is locked after the WHO Sign-in.');
  }
  if (!(['Requested', 'Scheduled', 'InPreOp', 'Postponed'] as SurgeryStatus[]).includes(detail.status)) {
    return refuse('The pre-operative checklist can no longer be changed for this booking.');
  }
  return ALLOWED;
}

export function receiveGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  switch (detail.status) {
    case 'Scheduled':
      return ALLOWED;
    case 'Requested':
    case 'Postponed':
      return refuse('Schedule the surgery first.');
    case 'InPreOp':
      return refuse('The patient has already been received.');
    default:
      return refuse('Only a scheduled surgery can be received in pre-op.');
  }
}

/** Knife to skin — never before the WHO Sign-in and Time-out, nor without the patient's consent (`SurgeryBooking.Start`). */
export function startGate(detail: GateInput): Gate {
  switch (detail.status) {
    case 'Scheduled':
    case 'InPreOp':
      break;
    case 'InProgress':
      return refuse('Surgery has already started.');
    case 'Completed':
      return refuse('Surgery is complete.');
    case 'Cancelled':
      return refuse('This booking is cancelled.');
    default:
      return refuse('Schedule the surgery first.');
  }
  const missing = (['SignIn', 'TimeOut'] as SafetyPhase[]).filter((phase) => !isPhaseComplete(detail, phase));
  if (missing.length > 0) {
    return refuse(
      `Surgery cannot start until the ${missing.map((p) => phaseLabel[p]).join(' and ')} ${missing.length > 1 ? 'are' : 'is'} complete.`,
    );
  }
  return consentGate(detail);
}

// ---------------------------------------------------------------- consent before surgery and anaesthesia

/** The consents needed that are not on record (server `SurgicalConsentStatus.Missing`). */
export function missingConsents(consent: SurgicalConsentDto): SurgicalConsentCheck[] {
  return consent.checks.filter((check) => check.state !== 'Recorded' && check.state !== 'NotNeeded');
}

/** "consent to anaesthesia": one consent the operation needs, in the screen's words. */
function consentWhat(kind: SurgicalConsentKind): string {
  return kind === 'Procedure' ? 'consent to the operation (high-risk procedure consent)' : 'consent to anaesthesia';
}

/** Why one consent the operation needs is not on record, as the server says it (`SurgicalConsent.Describe`). */
export function consentProblem(check: SurgicalConsentCheck): string {
  const what = consentWhat(check.kind);
  switch (check.state) {
    case 'BeforeBooking':
      return `${what} was recorded before this operation was booked; take it again for this operation`;
    case 'Withdrawn':
      return `${what} was withdrawn`;
    case 'Refused':
      return `the patient refused ${what}`;
    case 'OtherOperation':
      return `${what} was relied on by another operation${check.consent?.reliedOnBy ? ` (${check.consent.reliedOnBy})` : ''}; take it again for this operation`;
    default:
      return `${what} is not recorded`;
  }
}

/**
 * The consent the patient said no to, their latest word on it since the booking: a refusal, or a withdrawal (server
 * `SurgicalConsentStatus.No`, a refusal before a withdrawal). No emergency waiver is given or stands against it.
 */
export function saidNoConsent(consent: SurgicalConsentDto): SurgicalConsentCheck | null {
  return (
    consent.checks.find((check) => check.state === 'Refused') ??
    consent.checks.find((check) => check.state === 'Withdrawn') ??
    null
  );
}

/**
 * "The patient refused consent to anaesthesia: an emergency does not override a refusal. …", or withdrew it (server
 * `surgery.consent_refused`, `surgery.consent_withdrawn`).
 */
export function saidNoNotice(check: SurgicalConsentCheck): string {
  const [did, what] = check.state === 'Refused' ? ['refused', 'a refusal'] : ['withdrew', 'a withdrawal'];
  return `The patient ${did} ${consentWhat(check.kind)}: an emergency does not override ${what}. If the patient now agrees, record the new consent at the front office.`;
}

/**
 * "Recorded by Priya R; signed form attached": who recorded a consent at the front office, and whether the signed form is
 * attached to it.
 */
export function consentRecordedBy(record: ConsentOnRecord): string {
  const by = record.capturedByName ? `Recorded by ${record.capturedByName}` : 'Who recorded it is not known';
  return `${by}; ${record.signedFormAttached ? 'signed form attached' : 'no signed form attached'}`;
}

/**
 * "The patient refused this on 21 Nov 2026, 9:30 am (recorded by Dr X), before this operation was booked.": a refusal
 * recorded before the booking, which the gates do not count (server `EarlierRefusal`); null when there is none.
 */
export function earlierRefusalNotice(check: SurgicalConsentCheck): string | null {
  const earlier = check.earlierRefusal;
  if (!earlier) {
    return null;
  }
  const by = earlier.recordedByName ? ` (recorded by ${earlier.recordedByName})` : '';
  return `The patient refused this on ${formatDateTime(earlier.recordedAt)}${by}, before this operation was booked.`;
}

/**
 * The patient's consent as the gates read it (server `SurgicalConsent`): "consent signed", the WHO Sign-in and Time-out and
 * the incision wait for the consents the front office records, or a waiver in an emergency, which never stands against the
 * patient's refusal or withdrawal. A booking read without the consent (an older server) is left to the server, which
 * checks it anyway.
 */
export function consentGate(detail: Pick<SurgeryDetail, 'consent'>): Gate {
  const consent = detail.consent;
  if (!consent || consent.satisfied) {
    return ALLOWED;
  }
  const problems = missingConsents(consent).map(consentProblem).join('; ');
  const no = saidNoConsent(consent);
  const next = no
    ? no.state === 'Refused'
      ? ' An emergency does not override the patient’s refusal: if the patient now agrees, record the new consent at the front office.'
      : ' An emergency does not override a consent the patient withdrew: if the patient now agrees, record the new consent at the front office.'
    : consent.waiver
      ? ' Record it under the patient’s consents at the front office: consent was waived in an emergency, but the booking is no longer an emergency.'
      : ' Record it under the patient’s consents at the front office. In an emergency the operating surgeon may go ahead without it, with a second doctor who agrees.';
  return refuse(`The patient's consent is not recorded for this operation: ${problems}.${next}`);
}

/**
 * The consent line of a booking's details. Before the incision: where the patient's consent stands now (recorded, waived
 * in an emergency, or what is missing). Once the operation has started: what the incision relied on, the consents recorded
 * then (kept on the booking) or the emergency waiver for a consent it needed and did not rely on (the consent to
 * anaesthesia alone, it may be), never today's consents read back over a past operation, so a consent withdrawn afterwards
 * does not make it look unconsented. An operation started before the theatre read consents has neither, and nothing is
 * said.
 */
export function consentSummary(detail: Pick<SurgeryDetail, 'consent' | 'startedAt'>): string | null {
  const consent = detail.consent;
  if (!consent) {
    return null;
  }
  const waiver = consent.waiver;
  const waived = waiver
    ? `Waived in an emergency by ${waiver.surgeonName}, ${waiver.concurringName} agreeing (${formatDateTime(waiver.at)}): ${waiver.reason}`
    : null;
  if (detail.startedAt) {
    // The consents the operation needed do not change once it has started (server `SurgeryBooking.ReliesOnWaiver`).
    const anaesthesiaNeeded = consent.checks.some(
      (check) => check.kind === 'Anaesthesia' && check.state !== 'NotNeeded',
    );
    if (consent.procedureConsentId && (!anaesthesiaNeeded || consent.anaesthesiaConsentId)) {
      return 'Recorded at the front office before the incision';
    }
    return consent.procedureConsentId && waived
      ? `Consent to the operation recorded at the front office before the incision; consent to anaesthesia ${waived.charAt(0).toLowerCase()}${waived.slice(1)}`
      : waived;
  }
  if (consent.recorded) {
    return 'Recorded at the front office';
  }
  if (waived && consent.satisfied) {
    return waived;
  }
  const missing = `Not recorded: ${missingConsents(consent).map(consentProblem).join('; ')}`;
  if (!waived) {
    return missing;
  }
  const no = saidNoConsent(consent);
  if (!no) {
    return `${missing}. Consent was waived in an emergency, but the booking is no longer an emergency`;
  }
  return no.state === 'Refused'
    ? `${missing}. Consent was waived in an emergency, but the patient refused, and an emergency does not override a refusal`
    : `${missing}. Consent was waived in an emergency, but the patient withdrew consent, and an emergency does not override a withdrawal`;
}

/**
 * Whether the signed-in user may take an emergency operation ahead without consent (server `SurgeryBooking.WaiveConsent`):
 * the operating surgeon of an emergency booking that has not started, while the consent is not recorded, once, and never
 * against the patient's refusal or withdrawal.
 */
export function waiverGate(
  detail: Pick<SurgeryDetail, 'consent' | 'priority' | 'status' | 'surgeonId'>,
  practitionerId: string | null | undefined,
): Gate {
  const consent = detail.consent;
  if (!consent || consent.recorded) {
    return refuse("The patient's consent is recorded: there is nothing to waive.");
  }
  if (consent.waiver) {
    return refuse(
      `Consent was already waived by ${consent.waiver.surgeonName}, ${consent.waiver.concurringName} agreeing.`,
    );
  }
  const no = saidNoConsent(consent);
  if (no) {
    return refuse(saidNoNotice(no));
  }
  if (!(['Requested', 'Scheduled', 'InPreOp'] as SurgeryStatus[]).includes(detail.status)) {
    return refuse(
      'Consent is waived only before the operation starts, for a booking that is neither closed nor postponed.',
    );
  }
  if (detail.priority !== 'Emergency') {
    return refuse(
      "Only an emergency operation goes ahead without the patient's consent. Record the consent at the front office, or mark the booking an emergency if it is one.",
    );
  }
  if (!practitionerId || practitionerId !== detail.surgeonId) {
    return refuse(
      'Only the operating surgeon of this case goes ahead without consent, from their own sign-in, with a second doctor who agrees.',
    );
  }
  return ALLOWED;
}

/**
 * Moving this scheduled operation to another theatre or start ends the consent waived in an emergency: the operation waits,
 * so the consent is taken for the new slot, or waived again (server `SurgeryBooking.Schedule`).
 */
export function moveEndsWaiver(booking: Pick<SurgeryDetail, 'consent' | 'status'>): boolean {
  return booking.status === 'Scheduled' && booking.consent?.waiver != null;
}

/**
 * Another operating surgeon ends the consent waived in an emergency: it was the first surgeon's judgement, and the new one
 * waives it again, with a second doctor, if the operation still cannot wait (server `SurgeryBooking.AssignSurgeon`).
 */
export function surgeonChangeEndsWaiver(
  booking: Pick<SurgeryDetail, 'consent' | 'surgeonId'>,
  surgeonId: string | null,
): boolean {
  return booking.consent?.waiver != null && surgeonId !== booking.surgeonId;
}

/** Wheels-out — never before the WHO Sign-out (`SurgeryBooking.Complete`). */
export function completeGate(detail: GateInput): Gate {
  if (detail.status === 'Completed') {
    return refuse('Surgery is already complete.');
  }
  if (detail.status !== 'InProgress') {
    return refuse('Only a surgery in progress can be completed.');
  }
  if (!isPhaseComplete(detail, 'SignOut')) {
    return refuse('Surgery cannot be completed until the WHO Sign-out is complete.');
  }
  return ALLOWED;
}

export function scheduleGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['Requested', 'Scheduled', 'Postponed'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse('Only requested, scheduled or postponed surgeries can be given a slot.');
}

export function planGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['Requested', 'Scheduled', 'InPreOp', 'Postponed'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse('The surgical plan cannot be changed once surgery has started or the booking is closed.');
}

export function postponeGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['Requested', 'Scheduled', 'InPreOp'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse('Only surgeries that have not started can be postponed.');
}

export function cancelGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['InProgress', 'Completed', 'Cancelled'] as SurgeryStatus[]).includes(detail.status)
    ? refuse('A surgery that has started or is closed cannot be cancelled.')
    : ALLOWED;
}

// ---------------------------------------------------------------- maker-checker slice 10 (the theatre)

/** A side or level is booked, so the site is marked on the patient: never "not applicable" (server `SurgicalSite.NeedsMarking`). */
export function siteNeedsMarking(
  laterality: SurgeryDetail['laterality'] | null,
  level: string | null | undefined,
): boolean {
  return (laterality !== null && laterality !== 'NotApplicable') || Boolean(level?.trim());
}

/** What the WHO checklist confirms: the procedure and where (server `SurgicalSite`). */
export interface SitePlan {
  procedureServiceItemId: string | null;
  procedureName: string;
  laterality: SurgeryDetail['laterality'] | null;
  siteLevel: string | null;
}

/** The plan names another procedure, side or level than the booking (server `SurgicalSite.Differs`). */
export function siteDiffers(
  booking: Pick<SurgeryDetail, 'procedureServiceItemId' | 'procedureName' | 'laterality' | 'siteLevel'>,
  plan: SitePlan,
): boolean {
  const same = (a: string | null | undefined, b: string | null | undefined) =>
    (a ?? '').trim().toLowerCase() === (b ?? '').trim().toLowerCase();
  return (
    booking.procedureServiceItemId !== plan.procedureServiceItemId ||
    !same(booking.procedureName, plan.procedureName) ||
    booking.laterality !== (plan.laterality ?? 'NotApplicable') ||
    !same(booking.siteLevel, plan.siteLevel)
  );
}

/**
 * Saving this plan voids the WHO Sign-in (and the Time-out, if done): the side, level or procedure changes after the
 * Sign-in. Only the operating surgeon or the anaesthetist may then save it, with a reason (server `SiteChangeRule`).
 */
export function siteChangeVoidsChecklist(
  booking: Pick<
    SurgeryDetail,
    'procedureServiceItemId' | 'procedureName' | 'laterality' | 'siteLevel' | 'safetyChecklist'
  >,
  plan: SitePlan,
): boolean {
  return isPhaseComplete(booking, 'SignIn') && siteDiffers(booking, plan);
}

/** The anaesthesia technique and the priority of a plan (server `TechniqueOrPriorityChangeRule`). */
export interface CarePlan {
  anaesthesiaType: AnaesthesiaType;
  priority: SurgeryPriority;
}

/**
 * Saving this plan changes the anaesthesia technique or the priority after the WHO Sign-in. Both bear on the patient's
 * consent (local anaesthesia needs no consent to anaesthesia; only an emergency goes ahead without consent), so only the
 * operating surgeon or the anaesthetist may then save it, with a reason; the checklist stands (server
 * `TechniqueOrPriorityChangeRule`).
 */
export function careChangeAfterSignIn(
  booking: Pick<SurgeryDetail, 'anaesthesiaType' | 'priority' | 'safetyChecklist'>,
  plan: CarePlan,
): boolean {
  return (
    isPhaseComplete(booking, 'SignIn') &&
    (booking.anaesthesiaType !== plan.anaesthesiaType || booking.priority !== plan.priority)
  );
}

/**
 * The plan turns to local or topical anaesthesia after a Sign-in done under anaesthesia given by an anaesthetist: the
 * patient's consent to anaesthesia, needed then, stays needed (server `SurgeryBooking.AnaesthesiaConsentRequiredAtSignIn`).
 */
export function keepsAnaesthesiaConsent(
  booking: Pick<SurgeryDetail, 'consent'>,
  plan: Pick<CarePlan, 'anaesthesiaType'>,
): boolean {
  return booking.consent?.anaesthesiaConsentRequiredAtSignIn === true && givenBySurgeon(plan.anaesthesiaType);
}

/** The signed-in user is the case's operating surgeon or anaesthetist, who alone change the site after the Sign-in. */
export function onCaseTeam(
  booking: Pick<SurgeryDetail, 'surgeonId' | 'anaesthetistId'>,
  practitionerId: string | null | undefined,
): boolean {
  return Boolean(practitionerId) && (practitionerId === booking.surgeonId || practitionerId === booking.anaesthetistId);
}

/**
 * Implants and consumables are corrected, not removed, once the operation has started (server
 * `SurgeryBooking.MaterialsLocked`).
 */
export function materialsLocked(detail: Pick<SurgeryDetail, 'status' | 'startedAt' | 'safetyChecklist'>): boolean {
  return (
    Boolean(detail.startedAt) ||
    detail.status === 'InProgress' ||
    detail.status === 'Completed' ||
    isPhaseComplete(detail, 'SignOut')
  );
}

/** Counts are recorded on the day of surgery until the Sign-out (server `SurgicalCounts.CanRecord`). */
export function countsGate(detail: Pick<SurgeryDetail, 'status' | 'safetyChecklist'>): Gate {
  if (isPhaseComplete(detail, 'SignOut')) {
    return refuse('The counts closed with the WHO Sign-out.');
  }
  return (['Scheduled', 'InPreOp', 'InProgress'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse('Counts are recorded on the day of surgery, until the WHO Sign-out.');
}

export function materialsGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return (['InPreOp', 'InProgress', 'Completed'] as SurgeryStatus[]).includes(detail.status)
    ? ALLOWED
    : refuse(
        'Implants and consumables are recorded from pre-op (after the patient is received) until the surgery is completed.',
      );
}

export function operationNoteGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return detail.status === 'InProgress' || detail.status === 'Completed'
    ? ALLOWED
    : refuse('The operation note is written once surgery has started.');
}

export function anaesthesiaGate(detail: Pick<SurgeryDetail, 'status'>): Gate {
  return detail.status === 'Requested' || detail.status === 'Cancelled'
    ? refuse('The anaesthesia record is kept for scheduled surgeries.')
    : ALLOWED;
}

// ---------------------------------------------------------------- implants and consumables from the OT store

/** Why the OT store cannot be searched: the facility has no active OT store, or more than one (422 from the server). */
export type OtStoreProblem = 'no-store' | 'ambiguous';

export function otStoreProblem(error: unknown): OtStoreProblem | null {
  const code = isApiError(error) ? error.code : undefined;
  return code === 'stock.no_store' ? 'no-store' : code === 'stock.store_ambiguous' ? 'ambiguous' : null;
}

/** The listed batch whose lot is the one on the form (case and spaces ignored), if any. */
export function batchForLot(item: Pick<OtStoreItem, 'batches'> | null, lotNumber: string): OtStoreBatch | null {
  const lot = lotNumber.trim().toUpperCase();
  return lot && item ? (item.batches.find((b) => b.batchNumber.trim().toUpperCase() === lot) ?? null) : null;
}

/** What the OT store can issue: a named lot is issued only from that batch, otherwise FEFO draws on all the stock. */
export function issuableQuantity(item: Pick<OtStoreItem, 'available' | 'batches'>, lotNumber: string): number {
  return batchForLot(item, lotNumber)?.available ?? item.available;
}

/**
 * The price per unit once a batch is chosen: the most that batch may be charged (see `packPrice`), unless the user typed
 * a price of their own (anything other than the price filled in earlier), which is kept.
 */
export function priceForBatch(current: number | null, filledPrice: number | null, packLimit: number): number {
  return current === null || current === filledPrice ? packLimit : current;
}

/**
 * The most a unit from a pack may be charged: its MRP, or a price-controlled implant's NPPA ceiling price when that is
 * lower, as for a pack printed before the ceiling was cut (the server refuses more).
 */
export function packPrice(mrp: number, ceilingPrice: number | null | undefined): number {
  return ceilingPrice && ceilingPrice > 0 && ceilingPrice < mrp ? ceilingPrice : mrp;
}

/** "Lot B123 · expires 31 Mar 2027 · 4 Nos available · MRP ₹1,250.00". */
export function batchLabel(batch: OtStoreBatch, baseUnit: string): string {
  return [
    `Lot ${batch.batchNumber}`,
    batch.expiryDate ? `expires ${formatDateOnly(batch.expiryDate)}` : 'no expiry date',
    `${formatNumber(batch.available)} ${baseUnit} available`,
    `MRP ${formatInr(batch.mrp)}`,
  ].join(' · ');
}

// ---------------------------------------------------------------- day-of-surgery steps

export type StepId = 'schedule' | 'pre-op' | 'receive' | 'sign-in' | 'time-out' | 'start' | 'sign-out' | 'complete';
export type StepState = 'done' | 'current' | 'upcoming' | 'skipped' | 'stopped';

export const STEP_IDS: readonly StepId[] = [
  'schedule',
  'pre-op',
  'receive',
  'sign-in',
  'time-out',
  'start',
  'sign-out',
  'complete',
];

export interface SurgeryStep {
  id: StepId;
  title: string;
  moment: string;
  state: StepState;
  at: string | null;
}

export function lastRecordedAt(items: ChecklistItemDto[]): string | null {
  const times = items.map((i) => i.recordedAt).filter((at): at is string => Boolean(at));
  return times.length === 0 ? null : times.reduce((latest, at) => (Date.parse(at) > Date.parse(latest) ? at : latest));
}

type StepInput = Pick<
  SurgeryDetail,
  | 'status'
  | 'scheduledStart'
  | 'preOpComplete'
  | 'preOpChecklist'
  | 'safetyChecklist'
  | 'receivedInPreOpAt'
  | 'startedAt'
  | 'completedAt'
>;

/** The day of surgery in order. "Received in pre-op" is optional: it shows as skipped when later steps happened. */
export function surgerySteps(detail: StepInput): SurgeryStep[] {
  const phase = (p: SafetyPhase) => detail.safetyChecklist.find((x) => x.phase === p);
  const slotHeld =
    detail.scheduledStart !== null &&
    !(['Requested', 'Postponed', 'Cancelled'] as SurgeryStatus[]).includes(detail.status);
  const raw: { id: StepId; title: string; moment: string; done: boolean; at: string | null; optional?: boolean }[] = [
    {
      id: 'schedule',
      title: 'Theatre and time booked',
      moment: 'OT coordinator',
      done: slotHeld,
      at: detail.scheduledStart,
    },
    {
      id: 'pre-op',
      title: 'Pre-operative checklist',
      moment: 'Ward or pre-op nurse',
      done: detail.preOpComplete,
      at: lastRecordedAt(detail.preOpChecklist),
    },
    {
      id: 'receive',
      title: 'Received in pre-op',
      moment: 'Pre-op holding area',
      done: detail.receivedInPreOpAt !== null,
      at: detail.receivedInPreOpAt,
      optional: true,
    },
    {
      id: 'sign-in',
      title: phaseLabel.SignIn,
      moment: 'Before induction of anaesthesia',
      done: phase('SignIn')?.isComplete ?? false,
      at: phase('SignIn')?.completedAt ?? null,
    },
    {
      id: 'time-out',
      title: phaseLabel.TimeOut,
      moment: 'Before skin incision',
      done: phase('TimeOut')?.isComplete ?? false,
      at: phase('TimeOut')?.completedAt ?? null,
    },
    {
      id: 'start',
      title: 'Surgery started',
      moment: 'Knife to skin',
      done: detail.startedAt !== null,
      at: detail.startedAt,
    },
    {
      id: 'sign-out',
      title: phaseLabel.SignOut,
      moment: 'Before the patient leaves the theatre',
      done: phase('SignOut')?.isComplete ?? false,
      at: phase('SignOut')?.completedAt ?? null,
    },
    {
      id: 'complete',
      title: 'Patient out of theatre',
      moment: 'Surgery complete',
      done: detail.completedAt !== null,
      at: detail.completedAt,
    },
  ];

  let currentFound = false;
  return raw.map((step, index) => {
    const { optional, done, ...rest } = step;
    if (done) {
      return { ...rest, state: 'done' as const };
    }
    if (optional && raw.slice(index + 1).some((later) => later.done)) {
      return { ...rest, state: 'skipped' as const };
    }
    if (detail.status === 'Cancelled') {
      return { ...rest, state: 'stopped' as const };
    }
    if (!currentFound) {
      currentFound = true;
      return { ...rest, state: 'current' as const };
    }
    return { ...rest, state: 'upcoming' as const };
  });
}

/** The step to open by default: the first one still to do (or the last step when everything is done). */
export function currentStepId(detail: StepInput): StepId {
  const steps = surgerySteps(detail);
  return steps.find((s) => s.state === 'current')?.id ?? (detail.status === 'Completed' ? 'complete' : 'schedule');
}

export function whoProgress(item: { signInDone: boolean; timeOutDone: boolean; signOutDone: boolean }): number {
  return [item.signInDone, item.timeOutDone, item.signOutDone].filter(Boolean).length;
}

/** "Total knee replacement — LEFT". */
export function procedureWithSide(name: string, laterality: SurgeryDetail['laterality']): string {
  const side = sideShort(laterality);
  return side ? `${name} — ${side}` : name;
}

// ---------------------------------------------------------------- scheduling

/** Half-open overlap like the server: a case ending at 10:00 does not clash with one starting at 10:00. */
export function overlaps(aStart: string, aEnd: string, bStart: string, bEnd: string): boolean {
  return Date.parse(aStart) < Date.parse(bEnd) && Date.parse(bStart) < Date.parse(aEnd);
}

/** Bookings on the board that would clash with a proposed slot (a hint only; the server decides under a lock). */
export function localClashes(
  slot: { start: string; end: string; bookingId?: string | null },
  bookings: readonly OtCalendarBooking[],
): OtCalendarBooking[] {
  return bookings.filter(
    (b) =>
      b.id !== slot.bookingId &&
      BLOCKING_STATUSES.includes(b.status) &&
      b.start !== null &&
      b.end !== null &&
      overlaps(slot.start, slot.end, b.start, b.end),
  );
}

/** The clashes the server reported with a 409 `surgery.slot_conflict`, or null for any other error. */
export function slotConflicts(error: unknown): SchedulingConflict[] | null {
  if (!isApiError(error) || error.code !== 'surgery.slot_conflict') {
    return null;
  }
  return Array.isArray(error.data) ? (error.data as SchedulingConflict[]) : [];
}

export const DURATION_PRESETS: readonly number[] = [30, 60, 90, 120, 180, 240];

// ---------------------------------------------------------------- calendar layout

export interface TimeWindow {
  /** Minutes from IST midnight of the day shown. */
  start: number;
  end: number;
}

export const DEFAULT_WINDOW: TimeWindow = { start: 8 * 60, end: 20 * 60 };

/** Hours shown on the day board: 8 am–8 pm, widened to whole hours around any case that falls outside. */
export function dayWindow(bookings: readonly { start: string | null; end: string | null }[], date: string): TimeWindow {
  let start = DEFAULT_WINDOW.start;
  let end = DEFAULT_WINDOW.end;
  for (const booking of bookings) {
    if (!booking.start || !booking.end) {
      continue;
    }
    const s = Math.max(0, minutesIntoIstDay(booking.start, date));
    const e = Math.min(1440, minutesIntoIstDay(booking.end, date));
    if (e <= 0 || s >= 1440 || e <= s) {
      continue;
    }
    start = Math.min(start, Math.floor(s / 60) * 60);
    end = Math.max(end, Math.ceil(e / 60) * 60);
  }
  return { start, end };
}

export interface PlacedBlock<T> {
  item: T;
  /** Minutes from midnight, clipped to the window. */
  startMinute: number;
  endMinute: number;
  clippedStart: boolean;
  clippedEnd: boolean;
  /** Side-by-side position when cases overlap (an overrun), 0-based, out of `lanes`. */
  lane: number;
  lanes: number;
}

/** Positions one theatre's cases on the day board; overlapping cases share the column side by side. */
export function placeBlocks<T extends { start: string | null; end: string | null }>(
  items: readonly T[],
  date: string,
  window: TimeWindow,
): PlacedBlock<T>[] {
  const blocks = items
    .filter((item): item is T & { start: string; end: string } => item.start !== null && item.end !== null)
    .map((item) => {
      const s = minutesIntoIstDay(item.start, date);
      const e = minutesIntoIstDay(item.end, date);
      return {
        item,
        startMinute: Math.max(s, window.start),
        endMinute: Math.min(e, window.end),
        clippedStart: s < window.start,
        clippedEnd: e > window.end,
      };
    })
    .filter((b) => b.endMinute > b.startMinute)
    .sort((a, b) => a.startMinute - b.startMinute || a.endMinute - b.endMinute);

  const placed: PlacedBlock<T>[] = [];
  let cluster: ((typeof blocks)[number] & { lane: number })[] = [];
  let laneEnds: number[] = [];
  let clusterEnd = Number.NEGATIVE_INFINITY;

  const flush = () => {
    const lanes = Math.max(1, laneEnds.length);
    for (const block of cluster) {
      placed.push({ ...block, lanes });
    }
    cluster = [];
    laneEnds = [];
  };

  for (const block of blocks) {
    if (block.startMinute >= clusterEnd) {
      flush();
      clusterEnd = Number.NEGATIVE_INFINITY;
    }
    let lane = laneEnds.findIndex((end) => end <= block.startMinute);
    if (lane < 0) {
      lane = laneEnds.length;
      laneEnds.push(block.endMinute);
    } else {
      laneEnds[lane] = block.endMinute;
    }
    cluster.push({ ...block, lane });
    clusterEnd = Math.max(clusterEnd, block.endMinute);
  }
  flush();
  return placed;
}

// ---------------------------------------------------------------- utilisation

export interface UtilisationTotals {
  casesCompleted: number;
  casesCancelled: number;
  casesPostponed: number;
  bookedMinutes: number;
  actualMinutes: number;
  availableMinutes: number;
  utilisationPercent: number | null;
  firstCaseOnTimePercent: number | null;
}

export function percent(part: number, whole: number): number | null {
  return whole <= 0 ? null : Math.round((part * 1000) / whole) / 10;
}

/** Totals across theatres, computed the same way as the server's per-theatre figures. */
export function utilisationTotals(
  rows: readonly {
    metrics: {
      casesCompleted: number;
      casesCancelled: number;
      casesPostponed: number;
      bookedMinutes: number;
      actualMinutes: number;
      availableMinutes: number;
      firstCases: number;
      firstCasesOnTime: number;
    };
  }[],
): UtilisationTotals {
  const sum = (pick: (m: (typeof rows)[number]['metrics']) => number) =>
    rows.reduce((total, row) => total + pick(row.metrics), 0);
  const actual = sum((m) => m.actualMinutes);
  const available = sum((m) => m.availableMinutes);
  return {
    casesCompleted: sum((m) => m.casesCompleted),
    casesCancelled: sum((m) => m.casesCancelled),
    casesPostponed: sum((m) => m.casesPostponed),
    bookedMinutes: sum((m) => m.bookedMinutes),
    actualMinutes: actual,
    availableMinutes: available,
    utilisationPercent: percent(actual, available),
    firstCaseOnTimePercent: percent(
      sum((m) => m.firstCasesOnTime),
      sum((m) => m.firstCases),
    ),
  };
}

/** True once the WHO Sign-out is complete: the patient then belongs to the recovery room. */
export function isSignedOut(booking: SurgeryDetail): boolean {
  return booking.safetyChecklist.some((p) => p.phase === 'SignOut' && p.isComplete);
}
