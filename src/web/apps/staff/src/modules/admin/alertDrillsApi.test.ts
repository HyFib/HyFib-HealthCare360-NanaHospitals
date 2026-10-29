import { describe, expect, it } from 'vitest';
import { after, drillSteps, type AlertDrill, type AlertDrillMessage } from './alertDrillsApi';

const START = '2026-10-29T09:00:00Z';
const at = (seconds: number) => new Date(Date.parse(START) + seconds * 1000).toISOString();

const DELIVERED: AlertDrillMessage = {
  channel: 'Sms',
  recipient: '+91******0142',
  status: 'Delivered',
  queuedAt: at(150),
  sentAt: at(152),
  deliveredAt: at(170),
  error: null,
};

const drill = (patch: Partial<AlertDrill> = {}): AlertDrill => ({
  id: 'd1',
  code: '7F3A2C',
  startedAt: START,
  startedBy: 'IT Officer',
  state: 'acknowledged',
  toldAt: at(150),
  toldTo: 4,
  messages: [DELIVERED],
  firstReadAt: at(240),
  acknowledgedAt: at(410),
  acknowledgedBy: 'Hospital Admin',
  ...patch,
});

describe('alert drills', () => {
  it('says how long after the start, in seconds, minutes or hours', () => {
    expect(after(START, at(40))).toBe('40 s');
    expect(after(START, at(150))).toBe('3 min');
    expect(after(START, at(3600))).toBe('1 h');
    expect(after(START, at(3900))).toBe('1 h 5 min');
    expect(after(START, at(-5))).toBe('0 s');
  });

  it('lists each step of a drill that went through, with its timing', () => {
    expect(drillSteps(drill())).toEqual([
      { label: 'Told in the app', when: 'after 3 min (4 people)', tone: 'done' },
      { label: 'SMS to +91******0142, sent', when: 'after 3 min', tone: 'done' },
      { label: 'SMS to +91******0142, delivered', when: 'after 3 min', tone: 'done' },
      { label: 'Read in the app', when: 'after 4 min', tone: 'done' },
      { label: 'Acknowledged', when: 'after 7 min (Hospital Admin)', tone: 'done' },
    ]);
  });

  it('shows where an expired drill broke: an SMS the provider refused, and nobody acknowledging', () => {
    const steps = drillSteps(
      drill({
        state: 'expired',
        messages: [{ ...DELIVERED, status: 'Failed', sentAt: null, deliveredAt: null, error: 'No DLT template id.' }],
        firstReadAt: null,
        acknowledgedAt: null,
        acknowledgedBy: null,
      }),
    );

    expect(steps).toContainEqual({
      label: 'SMS to +91******0142',
      when: 'failed: No DLT template id.',
      tone: 'missed',
    });
    expect(steps.at(-1)).toEqual({ label: 'Acknowledged', when: 'never', tone: 'missed' });
  });

  it('waits on an open drill, and says when no on-call number was set', () => {
    const waiting = drillSteps(
      drill({ state: 'open', toldAt: null, messages: [], firstReadAt: null, acknowledgedAt: null }),
    );
    expect(waiting[0]).toEqual({ label: 'Told in the app', when: 'not yet', tone: 'waiting' });

    const noNumber = drillSteps(drill({ state: 'open', messages: [] }));
    expect(noNumber).toContainEqual({ label: 'Sent to IT on-call', when: 'no on-call number was set', tone: 'missed' });
  });
});
