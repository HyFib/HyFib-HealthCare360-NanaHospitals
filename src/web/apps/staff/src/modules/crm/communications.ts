/**
 * The patient's communications history (CRM-04): service messages, campaign messages and follow-up calls, as the
 * server's CommunicationEntry gives them. Messages carry a masked address and never their text.
 */
import { humanize } from '@hc360/core';
import {
  followUpOutcomeLabel,
  followUpStatusLabel,
  followUpTypeLabel,
  type FollowUpStatus,
  type FollowUpType,
  type RecordedFollowUpOutcome,
} from './labels';

export const COMMUNICATION_KINDS = ['Message', 'Campaign', 'FollowUpCall'] as const;
export type CommunicationKind = (typeof COMMUNICATION_KINDS)[number];
export const communicationKindLabel: Record<CommunicationKind, string> = {
  Message: 'Service message',
  Campaign: 'Campaign',
  FollowUpCall: 'Follow-up call',
};

export type MessageChannel = 'Sms' | 'WhatsApp' | 'Email' | 'InApp';
export const messageChannelLabel: Record<MessageChannel, string> = {
  Sms: 'SMS',
  WhatsApp: 'WhatsApp',
  Email: 'E-mail',
  InApp: 'In the app',
};

export type MessageStatus = 'Queued' | 'Sent' | 'Failed' | 'Skipped' | 'Delivered' | 'Suppressed';
const messageStatusLabel: Record<MessageStatus, string> = {
  Queued: 'Waiting to send',
  Sent: 'Sent',
  Failed: 'Failed',
  Skipped: 'Not sent',
  Delivered: 'Delivered',
  Suppressed: 'Not sent: opted out',
};

export interface CommunicationEntry {
  id: string;
  kind: CommunicationKind;
  occurredAt: string;
  channel: MessageChannel | null;
  templateKey: string | null;
  purpose: string | null;
  recipient: string | null;
  status: MessageStatus | null;
  sentAt: string | null;
  deliveredAt: string | null;
  campaignId: string | null;
  campaignName: string | null;
  skipReason: string | null;
  followUpId: string | null;
  followUpType: FollowUpType | null;
  followUpStatus: FollowUpStatus | null;
  callOutcome: RecordedFollowUpOutcome | null;
  callAttempts: number | null;
}

type Tone = 'danger' | 'warning' | 'success' | 'brand' | 'informative';

/** What the line was about: the campaign's name, the call's reason, or the template's description or key. */
export function communicationTitle(e: CommunicationEntry): string {
  if (e.kind === 'FollowUpCall') {
    return e.followUpType ? followUpTypeLabel[e.followUpType] : 'Follow-up call';
  }
  if (e.kind === 'Campaign' && e.campaignName) {
    return e.campaignName;
  }
  return e.purpose ?? templateLabel(e.templateKey);
}

/** "lab.report_ready" reads as "Lab report ready" when the template has no description. */
export function templateLabel(key: string | null): string {
  return key ? humanize(key.replace(/[._-]+/g, ' ')) : 'Message';
}

/** How far it got: the message's delivery state, or the call's attempts and last outcome. */
export function communicationOutcome(e: CommunicationEntry): { label: string; tone: Tone } {
  if (e.kind === 'FollowUpCall') {
    const attempts = e.callAttempts ?? 0;
    const tries = `${attempts} ${attempts === 1 ? 'call' : 'calls'}`;
    const outcome = e.callOutcome ? followUpOutcomeLabel[e.callOutcome] : 'No outcome yet';
    const reached = e.callOutcome === 'Reached' || e.callOutcome === 'AppointmentBooked';
    const stillOpen = e.followUpStatus === 'Open' || e.followUpStatus === 'InProgress';
    return {
      label: `${outcome} · ${tries}${stillOpen && e.followUpStatus ? ` · ${followUpStatusLabel[e.followUpStatus].toLowerCase()}` : ''}`,
      tone: reached ? 'success' : stillOpen ? 'warning' : 'informative',
    };
  }
  switch (e.status) {
    case 'Delivered':
    case 'Sent':
      return { label: messageStatusLabel[e.status], tone: 'success' };
    case 'Queued':
      return { label: messageStatusLabel.Queued, tone: 'brand' };
    case 'Failed':
      return { label: messageStatusLabel.Failed, tone: 'danger' };
    case 'Skipped':
    case 'Suppressed':
      return { label: messageStatusLabel[e.status], tone: 'warning' };
    default:
      return { label: '—', tone: 'informative' };
  }
}

/** The channel and masked address, or for a skipped campaign why it passed the patient over. */
export function communicationDetail(e: CommunicationEntry): string {
  if (e.kind === 'FollowUpCall') {
    return 'Phone call';
  }
  const parts = [e.channel ? messageChannelLabel[e.channel] : null, e.recipient, e.skipReason];
  return parts.filter(Boolean).join(' · ');
}
