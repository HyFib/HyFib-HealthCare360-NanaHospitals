import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HubConnection, type WebSocketLike } from './HubConnection';
import { RECORD_SEPARATOR } from './hubProtocol';

/** A scripted server end of a WebSocket. */
class FakeSocket implements WebSocketLike {
  readyState = 0;
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  readonly sent: string[] = [];
  closed = false;

  constructor(readonly url: string) {}

  send(data: string): void {
    this.sent.push(data);
  }

  close(): void {
    this.closed = true;
    this.readyState = 3;
  }

  open(): void {
    this.readyState = 1;
    this.onopen?.({} as Event);
  }

  receive(...records: object[]): void {
    this.onmessage?.({ data: records.map((r) => JSON.stringify(r) + RECORD_SEPARATOR).join('') } as MessageEvent);
  }

  drop(reason = ''): void {
    this.readyState = 3;
    this.onclose?.({ code: 1006, reason, wasClean: false } as CloseEvent);
  }

  sentMessages(): Record<string, unknown>[] {
    return this.sent.flatMap((data) => data.split(RECORD_SEPARATOR).filter(Boolean)).map((record) => JSON.parse(record) as Record<string, unknown>);
  }
}

function setup() {
  const sockets: FakeSocket[] = [];
  const connection = new HubConnection({
    path: '/hubs/telehealth',
    baseUrl: 'https://hc360.local/telehealth/1',
    accessTokenFactory: () => 'token-1',
    createWebSocket: (url) => {
      const socket = new FakeSocket(url);
      sockets.push(socket);
      return socket;
    },
  });
  return { connection, sockets };
}

/** Lets the async connect code reach the point where it waits for the socket. */
const flush = () => vi.advanceTimersByTimeAsync(0);

async function connect(connection: HubConnection, sockets: FakeSocket[]) {
  const started = connection.start();
  await flush();
  const socket = sockets.at(-1)!;
  socket.open();
  socket.receive({});
  await started;
  return socket;
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('HubConnection', () => {
  it('connects with the token and completes the JSON handshake', async () => {
    const { connection, sockets } = setup();
    const socket = await connect(connection, sockets);
    expect(socket.url).toBe('wss://hc360.local/hubs/telehealth?access_token=token-1');
    expect(socket.sentMessages()[0]).toEqual({ protocol: 'json', version: 1 });
    expect(connection.state).toBe('Connected');
  });

  it('fails to start when the server refuses the handshake', async () => {
    const { connection, sockets } = setup();
    const started = connection.start();
    await flush();
    sockets[0]!.open();
    sockets[0]!.receive({ error: 'Protocol not supported' });
    await expect(started).rejects.toThrow('Protocol not supported');
    expect(connection.state).toBe('Disconnected');
  });

  it('resolves and rejects invocations from completion messages', async () => {
    const { connection, sockets } = setup();
    const socket = await connect(connection, sockets);
    const join = connection.invoke<{ participantId: string }>('JoinAsClinician', 's1');
    const admit = connection.invoke('Admit', 's1', null);
    const [joinMessage, admitMessage] = socket.sentMessages().slice(1);
    expect(joinMessage).toMatchObject({ type: 1, target: 'JoinAsClinician', arguments: ['s1'] });
    socket.receive({ type: 3, invocationId: joinMessage!.invocationId, result: { participantId: 'p1' } });
    socket.receive({ type: 3, invocationId: admitMessage!.invocationId, error: "An unexpected error occurred invoking 'Admit' on the server. HubException: Nobody is waiting." });
    await expect(join).resolves.toEqual({ participantId: 'p1' });
    await expect(admit).rejects.toThrow(/Nobody is waiting/);
  });

  it('delivers server calls to handlers whatever the case of the name', async () => {
    const { connection, sockets } = setup();
    const received: unknown[] = [];
    connection.on<{ text: string }>('chatMessage', (message) => received.push(message.text));
    const socket = await connect(connection, sockets);
    socket.receive({ type: 1, target: 'chatmessage', arguments: [{ text: 'Hello doctor' }] }, { type: 6 });
    expect(received).toEqual(['Hello doctor']);
  });

  it('sends keep-alive pings and reconnects after a silent server', async () => {
    const { connection, sockets } = setup();
    const reconnecting = vi.fn();
    const reconnected = vi.fn();
    connection.onreconnecting(reconnecting);
    connection.onreconnected(reconnected);
    const first = await connect(connection, sockets);

    await vi.advanceTimersByTimeAsync(15_000);
    expect(first.sentMessages().some((m) => m.type === 6)).toBe(true);

    await vi.advanceTimersByTimeAsync(20_000);
    expect(reconnecting).toHaveBeenCalledOnce();
    expect(connection.state).toBe('Reconnecting');
    expect(first.closed).toBe(true);

    await vi.advanceTimersByTimeAsync(1_000);
    const second = sockets[1]!;
    second.open();
    second.receive({});
    await flush();
    expect(reconnected).toHaveBeenCalledOnce();
    expect(connection.state).toBe('Connected');
  });

  it('fails pending calls when the connection drops, and keeps retrying', async () => {
    const { connection, sockets } = setup();
    const socket = await connect(connection, sockets);
    const pending = connection.invoke('SendChat', 's1', 'hi');
    socket.drop('network');
    await expect(pending).rejects.toThrow('network');
    expect(connection.state).toBe('Reconnecting');

    await vi.advanceTimersByTimeAsync(1_000);
    sockets[1]!.drop();
    await vi.advanceTimersByTimeAsync(3_000);
    expect(sockets.length).toBe(3);
  });

  it('stops for good: no reconnect and the close callback runs', async () => {
    const { connection, sockets } = setup();
    const closed = vi.fn();
    connection.onclose(closed);
    const socket = await connect(connection, sockets);
    await connection.stop();
    expect(socket.closed).toBe(true);
    expect(closed).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(sockets.length).toBe(1);
    await expect(connection.send('Leave', 's1')).rejects.toThrow(/Not connected/);
  });

  it('honours a server close that forbids reconnecting', async () => {
    const { connection, sockets } = setup();
    const closed = vi.fn();
    connection.onclose(closed);
    const socket = await connect(connection, sockets);
    socket.receive({ type: 7, error: 'Session revoked', allowReconnect: false });
    expect(connection.state).toBe('Disconnected');
    expect(closed).toHaveBeenCalledWith(expect.objectContaining({ message: 'Session revoked' }));
  });
});
