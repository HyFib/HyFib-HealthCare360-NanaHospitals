import type * as SignalR from '@microsoft/signalr';
import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { RealtimeClient } from './realtime';

/** A stand-in for SignalR's connection: each start succeeds or fails as the test says, and the test closes it. */
class FakeConnection {
  state = 'Disconnected';
  readonly start = vi.fn(async () => {
    this.state = 'Connected';
  });
  readonly invoke = vi.fn(async () => undefined);
  readonly stop = vi.fn(async () => this.close());
  private readonly closed: (() => void)[] = [];
  private readonly handlers = new Map<string, ((...args: unknown[]) => void)[]>();

  on(name: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(name, [...(this.handlers.get(name) ?? []), handler]);
  }

  /** The server sends `name` to this connection. */
  receive(name: string, ...args: unknown[]): void {
    this.handlers.get(name)?.forEach((handler) => handler(...args));
  }

  onreconnecting(): void {}
  onreconnected(): void {}
  onclose(callback: () => void): void {
    this.closed.push(callback);
  }

  /** The connection drops for good: automatic reconnection gave up, or the server went away. */
  close(): void {
    this.state = 'Disconnected';
    this.closed.forEach((callback) => callback());
  }
}

const fake = vi.hoisted(() => ({ connection: null as unknown }));

vi.mock('@microsoft/signalr', async (importOriginal) => {
  const actual = await importOriginal<typeof SignalR>();
  class Builder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return fake.connection;
    }
  }
  return { ...actual, HubConnectionBuilder: Builder };
});

describe('RealtimeClient', () => {
  let connection: FakeConnection;
  let refreshed: Mock<() => void>;
  let client: RealtimeClient;

  beforeEach(() => {
    vi.useFakeTimers();
    connection = new FakeConnection();
    fake.connection = connection;
    refreshed = vi.fn<() => void>();
    client = new RealtimeClient('/hubs/events', () => 'token', refreshed);
  });

  afterEach(() => vi.useRealTimers());

  it('does not refresh on the first connection', async () => {
    await client.start();

    expect(connection.start).toHaveBeenCalledOnce();
    expect(refreshed).not.toHaveBeenCalled();
  });

  it('refreshes stale data once it has started again after the connection closed', async () => {
    await client.start();
    connection.close();

    await vi.advanceTimersByTimeAsync(5_000);

    expect(connection.start).toHaveBeenCalledTimes(2);
    expect(refreshed).toHaveBeenCalledOnce();
  });

  it('refreshes once the first start succeeds after failing', async () => {
    connection.start.mockRejectedValueOnce(new Error('Failed to fetch'));

    await client.start();
    expect(refreshed).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(5_000);

    expect(connection.start).toHaveBeenCalledTimes(2);
    expect(refreshed).toHaveBeenCalledOnce();
  });

  it('refreshes once, within seconds, when the server says events were lost though the connection stayed open', async () => {
    await client.start();

    // With several servers, every one that was cut off from Redis says so once it is back.
    connection.receive('resync');
    connection.receive('resync');
    await vi.advanceTimersByTimeAsync(3_000);

    expect(refreshed).toHaveBeenCalledOnce();
    expect(connection.start).toHaveBeenCalledOnce();

    connection.receive('resync');
    await vi.advanceTimersByTimeAsync(3_000);
    expect(refreshed).toHaveBeenCalledTimes(2);
  });

  it('subscribes again, and then refreshes, when the server says events were lost', async () => {
    await client.start();

    // The screen opened a ward while Redis was away: subscribing failed, and the screen took no notice.
    connection.invoke.mockRejectedValueOnce(
      new Error("An unexpected error occurred invoking 'Subscribe' on the server."),
    );
    client.subscribe('ward:4b1a0c2e-6d1f-4c7e-9a51-2f0e8d3b7c10');
    await vi.advanceTimersByTimeAsync(0);

    // Once Redis is back the server says so. The ward is subscribed to again before anything is refetched, so its events
    // arrive from then on and the refetch shows what was missed before.
    let subscribed = (): void => undefined;
    connection.invoke.mockImplementationOnce(
      () =>
        new Promise<undefined>((resolve) => {
          subscribed = () => resolve(undefined);
        }),
    );
    connection.receive('resync');
    await vi.advanceTimersByTimeAsync(3_000);

    expect(connection.invoke).toHaveBeenCalledTimes(2);
    expect(connection.invoke).toHaveBeenLastCalledWith('Subscribe', 'ward:4b1a0c2e-6d1f-4c7e-9a51-2f0e8d3b7c10');
    expect(refreshed).not.toHaveBeenCalled();

    subscribed();
    await vi.advanceTimersByTimeAsync(0);
    expect(refreshed).toHaveBeenCalledOnce();
  });

  it('does not refresh for the server once the app stopped the connection', async () => {
    await client.start();
    connection.receive('resync');
    await client.stop();

    await vi.advanceTimersByTimeAsync(3_000);

    expect(refreshed).not.toHaveBeenCalled();
  });

  it('does not refresh when the app stopped the connection itself and starts it again', async () => {
    await client.start();
    await client.stop();

    await vi.advanceTimersByTimeAsync(5_000);
    await client.start();

    expect(connection.start).toHaveBeenCalledTimes(2);
    expect(refreshed).not.toHaveBeenCalled();
  });
});
