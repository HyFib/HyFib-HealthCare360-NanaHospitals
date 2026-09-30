/**
 * Pure CRM rules mirrored from the Engagement server (LeadWorkflow, FollowUpTask, Campaign) plus the SMS length
 * maths, so screens can guide the executive before a call to the server. The server checks everything again.
 */
import type { CampaignStatus, FollowUpOutcome, LeadStatus } from './labels';

type Tone = 'danger' | 'warning' | 'success' | 'brand' | 'informative';

// ------------------------------------------------------------------ leads

/** Open pipeline columns on the board, in order. */
export const LEAD_BOARD_COLUMNS: LeadStatus[] = ['New', 'Contacted', 'Qualified'];

/**
 * Status moves a person can make (LeadWorkflow.CanMove): never to Converted (use Convert), never from Converted, a
 * lost lead reopens only as New or Contacted, and losing a lead needs a reason.
 */
export function leadMoves(from: LeadStatus): LeadStatus[] {
  switch (from) {
    case 'Converted':
      return [];
    case 'Lost':
      return ['New', 'Contacted'];
    default:
      return (['New', 'Contacted', 'Qualified', 'Lost'] as LeadStatus[]).filter((status) => status !== from);
  }
}

export function leadTone(status: LeadStatus): Tone {
  switch (status) {
    case 'Converted':
      return 'success';
    case 'Lost':
      return 'danger';
    case 'Qualified':
      return 'brand';
    case 'Contacted':
      return 'informative';
    default:
      return 'warning';
  }
}

export function isLeadOpen(status: LeadStatus): boolean {
  return status === 'New' || status === 'Contacted' || status === 'Qualified';
}

// ------------------------------------------------------------------ follow-up calls

/** Unanswered calls after which the task closes by itself (FollowUpTask.MaxAttempts). */
export const MAX_UNREACHABLE_ATTEMPTS = 3;

/** Whether recording this outcome closes the task (FollowUpTask.RecordAttempt). */
export function outcomeClosesTask(outcome: FollowUpOutcome, attemptsSoFar: number): boolean {
  if (outcome === 'CallBackLater') {
    return false;
  }
  if (outcome === 'NotReachable') {
    return attemptsSoFar + 1 >= MAX_UNREACHABLE_ATTEMPTS;
  }
  return true;
}

/** Outcomes that ask for the next call date. */
export function outcomeNeedsNextDate(outcome: FollowUpOutcome, attemptsSoFar: number): boolean {
  return !outcomeClosesTask(outcome, attemptsSoFar);
}

// ------------------------------------------------------------------ campaigns

export type CampaignAction = 'edit' | 'schedule' | 'unschedule' | 'cancel';

export function campaignActions(status: CampaignStatus, can: { manage: boolean; send: boolean }): CampaignAction[] {
  const actions: CampaignAction[] = [];
  if (status === 'Draft' && can.manage) {
    actions.push('edit');
  }
  if (status === 'Draft' && can.send) {
    actions.push('schedule');
  }
  if (status === 'Scheduled' && can.send) {
    actions.push('unschedule');
  }
  if ((status === 'Draft' || status === 'Scheduled' || status === 'Sending') && can.send) {
    actions.push('cancel');
  }
  return actions;
}

export function campaignTone(status: CampaignStatus): Tone {
  switch (status) {
    case 'Completed':
      return 'success';
    case 'Cancelled':
      return 'informative';
    case 'Sending':
      return 'brand';
    case 'Scheduled':
      return 'warning';
    default:
      return 'informative';
  }
}

/** Promotional messages go out only between 10:00 and 21:00 IST (TRAI); a time outside waits for the next window. */
export function inSendingWindow(istDateTimeInput: string): boolean {
  const match = /T(\d{2}):(\d{2})/.exec(istDateTimeInput);
  if (!match) {
    return false;
  }
  const minutes = Number(match[1]) * 60 + Number(match[2]);
  return minutes >= 10 * 60 && minutes < 21 * 60;
}

// GSM 03.38 characters that fit in a standard SMS (the extension characters count twice).
const GSM_BASIC = new Set(
  '@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !"#¤%&\'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà',
);
const GSM_EXTENDED = new Set('^{}\\[~]|€');

export interface SmsLength {
  characters: number;
  parts: number;
  unicode: boolean;
  perPart: number;
}

/**
 * Length of an SMS as the network counts it: 160 characters in one part (153 per part when split), or 70 / 67 when
 * the text needs Unicode (Tamil, Hindi, emoji…).
 */
export function smsLength(text: string): SmsLength {
  let unicode = false;
  let units = 0;
  for (const char of text) {
    if (GSM_BASIC.has(char)) {
      units += 1;
    } else if (GSM_EXTENDED.has(char)) {
      units += 2;
    } else {
      unicode = true;
      break;
    }
  }
  const characters = unicode ? [...text].length : units;
  const single = unicode ? 70 : 160;
  const multi = unicode ? 67 : 153;
  const parts = characters === 0 ? 0 : characters <= single ? 1 : Math.ceil(characters / multi);
  return { characters, parts, unicode, perPart: parts > 1 ? multi : single };
}

/** SMS campaign text limit (the server's 480 characters, three parts). */
export const SMS_CAMPAIGN_MAX = 480;

/** Percentage with one decimal ("12.5%"), or "—" when there is nothing to divide by. */
export function percent(part: number, whole: number): string {
  if (!whole) {
    return '—';
  }
  return `${(Math.round((part / whole) * 1000) / 10).toLocaleString('en-IN')}%`;
}
