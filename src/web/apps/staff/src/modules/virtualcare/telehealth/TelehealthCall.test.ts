import type * as Core from '@hc360/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TelehealthJoinResult, TelehealthMessageDto, WaitingParticipant } from './api';
import { TelehealthCall } from './TelehealthCall';

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
    participantId: 'doctor-1',
    role: 'Clinician',
    status: 'WaitingRoom',
    admitted: true,
    scheduledStartAt: '2026-10-21T06:00:00Z',
    practitionerName: 'Dr. Rao',
    patientName: 'Asha Verma',
    iceServers: [],
    waiting: [],
    recentMessages: [],
    ...patch,
  };
}

const waiting: WaitingParticipant = {
  participantId: 'patient-1',
  role: 'Patient',
  displayName: 'Asha',
  joinedAt: '2026-10-21T06:01:00Z',
};
const missed: TelehealthMessageDto = {
  id: 'message-1',
  senderRole: 'Patient',
  senderName: 'Asha',
  text: 'I am in the waiting room',
  sentAt: '2026-10-21T06:01:30Z',
};

describe('TelehealthCall', () => {
  let call: TelehealthCall | null = null;

  afterEach(() => call?.stop());

  function start(refreshed: TelehealthJoinResult): FakeHub {
    const hub = new FakeHub((method) => (method === 'JoinAsClinician' ? result() : refreshed));
    fake.hub = hub;
    call = new TelehealthCall({
      sessionId: 'session-1',
      hubPath: '/hubs/telehealth',
      iceServers: [],
      loadSession: vi.fn(),
      onSessionChanged: vi.fn(),
    });
    call.start();
    return hub;
  }

  it('reads the consultation again when told it missed messages, without joining again', async () => {
    const hub = start(result({ waiting: [waiting], recentMessages: [missed] }));
    await vi.waitFor(() => expect(call?.getSnapshot().phase).toBe('joined'));

    // With several servers, Redis was away while the call stayed connected.
    hub.receive('resync');

    await vi.waitFor(() => expect(call?.getSnapshot().messages).toEqual([missed]));
    expect(call?.getSnapshot().waiting).toEqual([waiting]);
    expect(hub.invoke.mock.calls.map(([method]) => method)).toEqual(['JoinAsClinician', 'Refresh']);
  });

  it('hangs up when the consultation ended meanwhile', async () => {
    const hub = start(result({ status: 'Completed' }));
    await vi.waitFor(() => expect(call?.getSnapshot().phase).toBe('joined'));

    hub.receive('resync');

    await vi.waitFor(() => expect(call?.getSnapshot().phase).toBe('ended'));
    expect(call?.getSnapshot().sessionStatus).toBe('Completed');
  });
});
