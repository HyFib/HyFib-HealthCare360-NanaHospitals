import type * as Core from '@hc360/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TelehealthJoinResult, TelehealthMessageDto } from '@/lib/types';
import { PatientCall } from './PatientCall';

/** The telehealth hub as the call sees it: it answers each method as the test says, and sends what the test sends. */
class FakeHub {
  state = 'Disconnected';
  private readonly handlers = new Map<string, ((payload: unknown) => void)[]>();

  constructor(private readonly answer: (method: string) => unknown) {}

  readonly start = vi.fn(async () => {
    this.state = 'Connected';
  });
  readonly stop = vi.fn(async () => {
    this.state = 'Disconnected';
  });
  readonly invoke = vi.fn(async (method: string) => this.answer(method));
  readonly send = vi.fn(async () => undefined);

  on(event: string, handler: (payload: unknown) => void): () => void {
    this.handlers.set(event, [...(this.handlers.get(event) ?? []), handler]);
    return () =>
      this.handlers.set(
        event,
        (this.handlers.get(event) ?? []).filter((h) => h !== handler),
      );
  }

  onreconnecting(): () => void {
    return () => undefined;
  }

  onreconnected(): () => void {
    return () => undefined;
  }

  onclose(): () => void {
    return () => undefined;
  }

  /** The hub sends `event` to this connection. */
  receive(event: string, payload?: unknown): void {
    this.handlers.get(event)?.forEach((handler) => handler(payload));
  }
}

const fake = vi.hoisted(() => ({ hub: null as unknown }));
vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  createHubClient: () => fake.hub,
}));

function result(patch: Partial<TelehealthJoinResult> = {}): TelehealthJoinResult {
  return {
    sessionId: 'session-1',
    participantId: 'guest-1',
    role: 'Guest',
    status: 'WaitingRoom',
    admitted: false,
    scheduledStartAt: '2026-10-21T06:00:00Z',
    practitionerName: 'Dr. Rao',
    patientName: 'Asha',
    iceServers: [],
    waiting: [],
    recentMessages: [],
    ...patch,
  };
}

const missed: TelehealthMessageDto = {
  id: 'message-1',
  senderRole: 'Clinician',
  senderName: 'Dr. Rao',
  text: 'I will be with you in a minute',
  sentAt: '2026-10-21T06:02:00Z',
};

describe('PatientCall', () => {
  let call: PatientCall | null = null;

  afterEach(() => call?.stop());

  function start(...refreshed: TelehealthJoinResult[]): FakeHub {
    const answers = [...refreshed];
    const hub = new FakeHub((method) => (method === 'JoinAsGuest' ? result() : answers.shift()));
    fake.hub = hub;
    call = new PatientCall({
      sessionId: 'session-1',
      hubPath: '/hubs/telehealth',
      identity: { kind: 'guest', token: 't' },
    });
    call.start();
    return hub;
  }

  it('goes into the call when told it missed being admitted, without joining again', async () => {
    const hub = start(result({ status: 'InProgress', admitted: true, recentMessages: [missed] }));
    await vi.waitFor(() => expect(call?.getSnapshot().phase).toBe('waiting'));

    // With several servers, Redis was away while the patient waited, and the doctor admitted them meanwhile.
    hub.receive('resync');

    await vi.waitFor(() => expect(call?.getSnapshot().phase).toBe('in-call'));
    expect(call?.getSnapshot().admitted).toBe(true);
    expect(call?.getSnapshot().messages).toEqual([missed]);
    expect(hub.invoke.mock.calls.map(([method]) => method)).toEqual(['JoinAsGuest', 'Refresh']);
  });

  it('ends the call when the consultation ended meanwhile', async () => {
    const hub = start(result({ status: 'Completed' }));
    await vi.waitFor(() => expect(call?.getSnapshot().phase).toBe('waiting'));

    hub.receive('resync');

    await vi.waitFor(() => expect(call?.getSnapshot().phase).toBe('ended'));
    expect(call?.getSnapshot().endReason).toBe('Completed');
  });
});
