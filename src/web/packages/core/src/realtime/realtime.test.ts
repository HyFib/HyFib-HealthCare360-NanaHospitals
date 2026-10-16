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

  on(): void {}
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

  it('does not refresh when the app stopped the connection itself and starts it again', async () => {
    await client.start();
    await client.stop();

    await vi.advanceTimersByTimeAsync(5_000);
    await client.start();

    expect(connection.start).toHaveBeenCalledTimes(2);
    expect(refreshed).not.toHaveBeenCalled();
  });
});
