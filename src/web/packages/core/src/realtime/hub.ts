import { HubConnectionBuilder, LogLevel, type HubConnectionState, type ILogger } from '@microsoft/signalr';

export type HubState = `${HubConnectionState}`;

/**
 * A SignalR hub connection for screens with their own hub (telehealth signalling). It wraps the official client, so it
 * gets WebSocket with Server-Sent Events and long-polling fallbacks, keep-alives and automatic reconnection. Listener
 * methods return a function that removes the listener.
 */
export interface HubClient {
  readonly state: HubState;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T = unknown>(method: string, ...args: unknown[]): Promise<T>;
  send(method: string, ...args: unknown[]): Promise<void>;
  on<T = unknown>(event: string, handler: (payload: T) => void): () => void;
  onreconnecting(callback: (error?: Error) => void): () => void;
  onreconnected(callback: () => void): () => void;
  onclose(callback: (error?: Error) => void): () => void;
}

/**
 * SignalR's own messages, warnings and errors only. Stopping a connection before it finished connecting (the screen was
 * closed, or the user signed out) is reported by SignalR as an error, but it is expected, so it is left out.
 */
export const hubLogger: ILogger = {
  log(level, message) {
    if (level < LogLevel.Warning || isStoppedWhileConnecting(message)) {
      return;
    }
    if (level >= LogLevel.Error) {
      console.error(`[SignalR] ${message}`);
    } else {
      console.warn(`[SignalR] ${message}`);
    }
  },
};

function isStoppedWhileConnecting(message: string): boolean {
  return (
    message.includes('stopped during negotiation') ||
    message.includes('stopped before the hub handshake could complete')
  );
}

/**
 * Reconnect at once, then after 2, 5, 10 and 20 seconds, then every 30 seconds, for as long as the screen is open. Up to
 * a second of jitter is added, so the screens a WiFi drop cut off together do not all reconnect in the same instant.
 */
export function hubReconnectDelay(previousRetryCount: number, random: () => number = Math.random): number {
  const steps = [0, 2_000, 5_000, 10_000, 20_000];
  return (steps[previousRetryCount] ?? 30_000) + Math.floor(random() * 1_000);
}

/** A null token (the session has ended) connects without one, and the hub refuses it: every hub call is authorised. */
export function createHubClient(
  path: string,
  accessTokenFactory: () => string | null | Promise<string | null>,
): HubClient {
  const connection = new HubConnectionBuilder()
    .withUrl(path, { accessTokenFactory: async () => (await accessTokenFactory()) ?? '' })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (context) => hubReconnectDelay(context.previousRetryCount),
    })
    .configureLogging(hubLogger)
    .build();

  const reconnecting = new Set<(error?: Error) => void>();
  const reconnected = new Set<() => void>();
  const closed = new Set<(error?: Error) => void>();
  connection.onreconnecting((error) => reconnecting.forEach((callback) => callback(error)));
  connection.onreconnected(() => reconnected.forEach((callback) => callback()));
  connection.onclose((error) => closed.forEach((callback) => callback(error)));

  const listen = <T>(set: Set<T>, callback: T) => {
    set.add(callback);
    return () => void set.delete(callback);
  };

  return {
    get state() {
      return connection.state as HubState;
    },
    start: () => connection.start(),
    stop: () => connection.stop(),
    invoke: <T>(method: string, ...args: unknown[]) => connection.invoke<T>(method, ...args),
    send: (method: string, ...args: unknown[]) => connection.send(method, ...args),
    on<T>(event: string, handler: (payload: T) => void) {
      const listener = (payload: T) => handler(payload);
      connection.on(event, listener);
      return () => connection.off(event, listener);
    },
    onreconnecting: (callback) => listen(reconnecting, callback),
    onreconnected: (callback) => listen(reconnected, callback),
    onclose: (callback) => listen(closed, callback),
  };
}

/** The server's own message for a failed hub call (HubException), or a plain fallback. */
export function hubErrorMessage(error: unknown): string {
  const text = error instanceof Error ? error.message : typeof error === 'string' ? error : '';
  const marker = 'HubException: ';
  const index = text.lastIndexOf(marker);
  if (index >= 0) {
    return text.slice(index + marker.length).trim();
  }
  if (text.startsWith('An unexpected error occurred')) {
    return 'The server could not complete the request. Please try again.';
  }
  return text || 'The connection to the server failed.';
}
