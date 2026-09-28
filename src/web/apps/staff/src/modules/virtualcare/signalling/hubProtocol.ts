/**
 * The SignalR JSON hub protocol (version 1) as far as a client needs it: handshake, invocations, completions, pings
 * and close messages, each a JSON record terminated by the ASCII record separator. Specification:
 * https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md
 *
 * Pure functions only, so the framing is unit-tested without a server.
 */

export const RECORD_SEPARATOR = '\u001e';

export const MessageType = {
  Invocation: 1,
  StreamItem: 2,
  Completion: 3,
  StreamInvocation: 4,
  CancelInvocation: 5,
  Ping: 6,
  Close: 7,
} as const;

export interface InvocationMessage {
  type: 1;
  invocationId?: string;
  target: string;
  arguments: unknown[];
}

export interface CompletionMessage {
  type: 3;
  invocationId: string;
  result?: unknown;
  error?: string;
}

export interface PingMessage {
  type: 6;
}

export interface CloseMessage {
  type: 7;
  error?: string;
  allowReconnect: boolean;
}

export type HubMessage = InvocationMessage | CompletionMessage | PingMessage | CloseMessage;

export function frame(message: object): string {
  return JSON.stringify(message) + RECORD_SEPARATOR;
}

export const HANDSHAKE_REQUEST = frame({ protocol: 'json', version: 1 });
export const PING = frame({ type: MessageType.Ping });

/** An invocation; without an id it is fire-and-forget ("send"). */
export function invocationFrame(target: string, args: readonly unknown[], invocationId?: string): string {
  return frame(invocationId ? { type: MessageType.Invocation, invocationId, target, arguments: args } : { type: MessageType.Invocation, target, arguments: args });
}

/**
 * Splits incoming text into complete records. A WebSocket message normally carries whole records, but several may
 * arrive together (the handshake answer and the first ping) and a large one may be split, so a partial record is
 * kept until the rest arrives.
 */
export class RecordReader {
  private buffer = '';

  push(chunk: string): string[] {
    this.buffer += chunk;
    const parts = this.buffer.split(RECORD_SEPARATOR);
    this.buffer = parts.pop() ?? '';
    return parts.filter((part) => part.length > 0);
  }
}

/** The server answers the handshake with `{}` or `{"error": "…"}`. */
export function parseHandshakeResponse(record: string): { error: string | null } {
  try {
    const value = JSON.parse(record) as { error?: unknown } | null;
    return { error: value && typeof value.error === 'string' ? value.error : null };
  } catch {
    return { error: 'The server sent an unreadable handshake response.' };
  }
}

/** Parses one record; unknown or malformed messages (and streaming, which this client never uses) give null. */
export function parseHubMessage(record: string): HubMessage | null {
  let value: unknown;
  try {
    value = JSON.parse(record);
  } catch {
    return null;
  }
  if (!value || typeof value !== 'object') {
    return null;
  }
  const message = value as Record<string, unknown>;
  switch (message.type) {
    case MessageType.Invocation:
      return typeof message.target === 'string'
        ? {
            type: 1,
            target: message.target,
            invocationId: typeof message.invocationId === 'string' ? message.invocationId : undefined,
            arguments: Array.isArray(message.arguments) ? message.arguments : [],
          }
        : null;
    case MessageType.Completion:
      return typeof message.invocationId === 'string'
        ? { type: 3, invocationId: message.invocationId, result: message.result, error: typeof message.error === 'string' ? message.error : undefined }
        : null;
    case MessageType.Ping:
      return { type: 6 };
    case MessageType.Close:
      return { type: 7, error: typeof message.error === 'string' ? message.error : undefined, allowReconnect: message.allowReconnect === true };
    default:
      return null;
  }
}

/**
 * A user-facing sentence from a hub error. The server wraps `HubException` messages as
 * "An unexpected error occurred invoking 'X' on the server. HubException: <message>"; only the message is shown.
 */
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

/** Reconnect delays: at once, then 2, 5, 10 and 20 seconds, then every 30 seconds, each with up to 1 s of jitter. */
export function reconnectDelay(attempt: number, random: () => number = Math.random): number {
  const steps = [0, 2_000, 5_000, 10_000, 20_000];
  return (steps[attempt] ?? 30_000) + Math.floor(random() * 1_000);
}

/** WebSocket address of a hub on the page's own origin; browsers cannot send headers, so the token rides in the query. */
export function hubSocketUrl(path: string, accessToken: string | null, base: string): string {
  const url = new URL(path, base);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  if (accessToken) {
    url.searchParams.set('access_token', accessToken);
  }
  return url.toString();
}
