import { describe, expect, it } from 'vitest';
import {
  communicationDetail,
  communicationOutcome,
  communicationTitle,
  templateLabel,
  type CommunicationEntry,
} from './communications';

function entry(patch: Partial<CommunicationEntry>): CommunicationEntry {
  return {
    id: 'e1',
    kind: 'Message',
    occurredAt: '2026-10-16T04:30:00Z',
    channel: 'Sms',
    templateKey: 'lab.report_ready',
    purpose: null,
    recipient: '+91******3210',
    status: 'Delivered',
    sentAt: '2026-10-16T04:30:05Z',
    deliveredAt: '2026-10-16T04:30:09Z',
    campaignId: null,
    campaignName: null,
    skipReason: null,
    followUpId: null,
    followUpType: null,
    followUpStatus: null,
    callOutcome: null,
    callAttempts: null,
    ...patch,
  };
}

const call = (patch: Partial<CommunicationEntry>) =>
  entry({
    kind: 'FollowUpCall',
    channel: null,
    templateKey: null,
    recipient: null,
    status: null,
    followUpId: 'f1',
    followUpType: 'PostDischarge',
    followUpStatus: 'InProgress',
    callOutcome: 'NotReachable',
    callAttempts: 1,
    ...patch,
  });

describe('communications history', () => {
  it("names a message by its template's description, or else by its key", () => {
    expect(communicationTitle(entry({ purpose: 'Report ready (SMS)' }))).toBe('Report ready (SMS)');
    expect(communicationTitle(entry({}))).toBe('Lab report ready');
    expect(templateLabel('followup.reminder')).toBe('Followup reminder');
    expect(templateLabel(null)).toBe('Message');
  });

  it('names a campaign by the campaign and a call by its reason', () => {
    expect(communicationTitle(entry({ kind: 'Campaign', campaignName: 'Diabetes camp' }))).toBe('Diabetes camp');
    expect(communicationTitle(call({}))).toBe('After discharge');
  });

  it('says how far a message got', () => {
    expect(communicationOutcome(entry({}))).toEqual({ label: 'Delivered', tone: 'success' });
    expect(communicationOutcome(entry({ status: 'Failed' })).tone).toBe('danger');
    expect(communicationOutcome(entry({ status: 'Queued' })).label).toBe('Waiting to send');
    expect(communicationOutcome(entry({ status: 'Suppressed' })).label).toBe('Not sent: opted out');
  });

  it("says a call's last outcome, how many calls and whether it is still open", () => {
    expect(communicationOutcome(call({}))).toEqual({
      label: 'Not reachable · 1 call · calling again',
      tone: 'warning',
    });
    expect(
      communicationOutcome(call({ callOutcome: 'Reached', callAttempts: 2, followUpStatus: 'Completed' })),
    ).toEqual({
      label: 'Spoke to the patient · 2 calls',
      tone: 'success',
    });
  });

  it('shows the channel and masked number, and why a campaign passed the patient over', () => {
    expect(communicationDetail(entry({}))).toBe('SMS · +91******3210');
    expect(
      communicationDetail(
        entry({ kind: 'Campaign', channel: 'WhatsApp', recipient: null, status: 'Skipped', skipReason: 'No consent' }),
      ),
    ).toBe('WhatsApp · No consent');
    expect(communicationDetail(call({}))).toBe('Phone call');
  });
});
